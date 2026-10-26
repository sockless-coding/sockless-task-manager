using System.Globalization;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Processes.Dotnet;

/// <summary>One type's objects in a heap snapshot.</summary>
public sealed class HeapType(string fullName, long count, long bytes, long totalBytes)
{
    /// <summary>"Dictionary&lt;String, List&lt;Int32&gt;&gt;".</summary>
    public string Name { get; } = TypeName.Short(fullName);

    /// <summary>"System.Collections.Generic.Dictionary&lt;System.String, …&gt;".</summary>
    public string FullName { get; } = TypeName.Full(fullName);

    public string SizeText => Format.Bytes(bytes);

    /// <summary>"12,345 objects · 38%".</summary>
    public string Detail => $"{Format.Number(count)} {(count == 1 ? "object" : "objects")} · {Share}";

    private string Share
    {
        get
        {
            var percent = totalBytes > 0 ? 100.0 * bytes / totalBytes : 0;
            return percent is > 0 and < 1 ? "<1%" : Format.Percent(percent);
        }
    }
}

/// <summary>Every object on the heap at one moment, by type, largest first.</summary>
internal sealed record HeapSnapshot(IReadOnlyList<HeapType> Types, long Objects, long Bytes, DateTime TakenAt);

/// <summary>
/// Walks a .NET process's heap the way dotnet-gcdump does: a session with the runtime's
/// heap-dump keywords makes it run a full, blocking collection and describe every live object
/// (address, size, type) and every type as it goes. Only sizes by type are kept.
/// </summary>
internal sealed class HeapWalk
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const ulong GcKeyword = 0x1;
    private const ulong TypeKeyword = 0x80000;
    private const ulong GcHeapDumpKeyword = 0x100000;
    private const ulong GcHeapCollectKeyword = 0x800000;
    private const ulong GcHeapAndTypeNamesKeyword = 0x1000000;
    private const uint VerboseLevel = 5;

    // The runtime's event ids (ClrEtwAll.man).
    private const int GcStartEvent = 1;
    private const int GcEndEvent = 2;
    private const int BulkTypeEvent = 15;
    private const int BulkNodeEvent = 18;

    private const uint BackgroundGc = 1;

    /// <summary>The walk produces events fast; a smaller buffer would drop some.</summary>
    private const uint BufferMegabytes = 256;

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Gives up when nothing arrives for this long: the collection never started, or stalled.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(20);

    private readonly Dictionary<ulong, string> _types = [];
    private readonly Dictionary<ulong, (long Count, long Bytes)> _byType = [];
    private readonly TaskCompletionSource _collected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _gc = -1;
    private long _objects;
    private long _lastEventTicks = Environment.TickCount64;

    /// <summary>Objects described so far, for progress. Any thread.</summary>
    public long Objects => Interlocked.Read(ref _objects);

    /// <summary>Whether the collection has started, so it's walking rather than waiting.</summary>
    public bool HasStarted => Interlocked.Read(ref _gc) >= 0;

    public async Task<HeapSnapshot> RunAsync(int processId, CancellationToken cancel)
    {
        EventPipeSession session;
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            handshake.CancelAfter(HandshakeTimeout);
            session = await DiagnosticsPort.StartAsync(processId, RuntimeProvider,
                GcKeyword | TypeKeyword | GcHeapDumpKeyword | GcHeapCollectKeyword | GcHeapAndTypeNamesKeyword,
                VerboseLevel, null, BufferMegabytes, handshake.Token);
        }

        await using (session)
        {
            var reading = new NettraceReader(session.Stream).ReadAsync(OnEvent, cancel);

            // Wait for the collection to end, then stop: the runtime sends what it still holds and
            // ends the stream, so every object is in once reading finishes.
            while (!_collected.Task.IsCompleted)
            {
                await Task.WhenAny(reading, _collected.Task, Task.Delay(TimeSpan.FromSeconds(1), cancel));
                cancel.ThrowIfCancellationRequested();
                if (reading.IsCompleted)
                {
                    await reading;
                    throw new InvalidDataException("The process ended the heap walk early.");
                }

                if (Environment.TickCount64 - Interlocked.Read(ref _lastEventTicks) > StallTimeout.TotalMilliseconds)
                    throw new TimeoutException(HasStarted ? "The heap walk stalled." : "The runtime didn't start a heap walk.");
            }

            await session.StopAsync();
            await reading;
        }

        return Summarize();
    }

    /// <summary>Reading task.</summary>
    private void OnEvent(EventMetadata metadata, ReadOnlySpan<byte> payload)
    {
        if (metadata.Provider != RuntimeProvider)
            return;

        Interlocked.Exchange(ref _lastEventTicks, Environment.TickCount64);
        var reader = new SpanReader(payload);
        switch (metadata.EventId)
        {
            case GcStartEvent:
            {
                // Count, depth, reason, type. The first full, blocking collection is the walk.
                var count = reader.UInt32();
                var depth = reader.UInt32();
                reader.UInt32();
                var type = reader.UInt32();
                if (_gc < 0 && depth == 2 && type != BackgroundGc)
                    Interlocked.Exchange(ref _gc, count);
                break;
            }

            case GcEndEvent:
                if (_gc >= 0 && reader.UInt32() == _gc)
                    _collected.TrySetResult();
                break;

            case BulkTypeEvent:
            {
                // Count and CLR instance, then per type: id, module, name id, flags, element type,
                // name ("List`1[System.Int32]", already with its arguments), and its arguments' ids.
                var count = reader.UInt32();
                reader.Offset += sizeof(ushort);
                for (var i = 0; i < count; i++)
                {
                    var id = reader.UInt64();
                    reader.Offset += sizeof(ulong) + sizeof(uint) + sizeof(uint) + sizeof(byte);
                    _types[id] = reader.String();
                    var arguments = (int)reader.UInt32();
                    reader.Offset += arguments * sizeof(ulong);
                }

                break;
            }

            case BulkNodeEvent when _gc >= 0:
            {
                // Index, count and CLR instance, then per object: address, size, type id, edge count.
                reader.UInt32();
                var count = reader.UInt32();
                reader.Offset += sizeof(ushort);
                for (var i = 0; i < count; i++)
                {
                    reader.UInt64();
                    var size = (long)reader.UInt64();
                    var type = reader.UInt64();
                    reader.UInt64();

                    var (objects, bytes) = _byType.GetValueOrDefault(type);
                    _byType[type] = (objects + 1, bytes + size);
                }

                Interlocked.Add(ref _objects, count);
                break;
            }
        }
    }

    private HeapSnapshot Summarize()
    {
        // Types with the same name (loaded twice, in different load contexts) are one row.
        var byName = new Dictionary<string, (long Count, long Bytes)>(StringComparer.Ordinal);
        long objects = 0, bytes = 0;
        foreach (var (type, (count, size)) in _byType)
        {
            var name = _types.GetValueOrDefault(type) ?? $"Type 0x{type.ToString("X", CultureInfo.InvariantCulture)}";
            var (c, b) = byName.GetValueOrDefault(name);
            byName[name] = (c + count, b + size);
            objects += count;
            bytes += size;
        }

        var types = byName
            .OrderByDescending(t => t.Value.Bytes)
            .Select(t => new HeapType(t.Key, t.Value.Count, t.Value.Bytes, bytes))
            .ToArray();
        return new HeapSnapshot(types, objects, bytes, DateTime.Now);
    }
}

/// <summary>The runtime's type names, "Dictionary`2[System.String,System.Int32][]", as C# writes them.</summary>
internal static class TypeName
{
    public static string Full(string name) => Rewrite(name, shorten: false);

    /// <summary>Without namespaces: "Dictionary&lt;String, Int32&gt;[]".</summary>
    public static string Short(string name) => Rewrite(name, shorten: true);

    private static string Rewrite(string name, bool shorten)
    {
        var text = new System.Text.StringBuilder(name.Length);
        var generic = new Stack<bool>();
        var start = 0;
        for (var i = 0; i <= name.Length; i++)
        {
            var c = i < name.Length ? name[i] : ' ';
            if (c is not ('[' or ']' or ',' or '`' or ' '))
                continue;

            var identifier = name.AsSpan(start, i - start);
            if (shorten && identifier.LastIndexOf('.') is var dot and >= 0)
                identifier = identifier[(dot + 1)..];
            text.Append(identifier);

            switch (c)
            {
                case '`':
                    // The arity ("`2"); the arguments follow in brackets.
                    while (i + 1 < name.Length && char.IsAsciiDigit(name[i + 1]))
                        i++;
                    break;
                case '[':
                    // Arguments, unless it's an array's "[]", "[,]" or "[*]". Nested generic types
                    // ("Entry[System.String,System.Int32]") have arguments but no arity.
                    var isGeneric = i + 1 < name.Length && name[i + 1] is not (']' or ',' or '*');
                    generic.Push(isGeneric);
                    text.Append(isGeneric ? '<' : '[');
                    break;
                case ']':
                    text.Append(generic.Count > 0 && generic.Pop() ? '>' : ']');
                    break;
                case ',':
                    text.Append(generic.Count > 0 && generic.Peek() ? ", " : ",");
                    break;
            }

            start = i + 1;
        }

        return text.ToString();
    }
}
