using System.Collections.ObjectModel;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Processes.Dotnet;

/// <summary>One type's share of what the process allocated.</summary>
public sealed class AllocatedType(string type, long bytes, long totalBytes)
{
    public string Name { get; } = TypeName.Short(type);

    public string FullName { get; } = TypeName.Full(type);

    public string BytesText { get; } = Format.Bytes(bytes);

    public string ShareText { get; } = Share(bytes, totalBytes);

    internal long Bytes => bytes;

    private static string Share(long part, long whole)
    {
        var percent = whole > 0 ? 100.0 * part / whole : 0;
        return percent is > 0 and < 1 ? "<1%" : Format.Percent(percent);
    }
}

/// <summary>
/// What the process allocates, by type. The runtime samples: about every 100 KB allocated it
/// raises an event naming the type of the object that crossed the mark, and the 100 KB count
/// against that type. Over a few seconds of steady allocation that's a fair picture; a type that
/// allocates little may not show at all. Runs only while the .NET page is open.
/// </summary>
public sealed class AllocationFeed : RuntimeFeed
{
    private const ulong GcKeyword = 0x1;
    private const uint VerboseLevel = 5;
    private const int AllocationTickEvent = 10;

    private readonly Dictionary<string, long> _bytes = new(StringComparer.Ordinal);
    private long _total;
    private DateTime _since = DateTime.Now;
    private string _summary = "Nothing sampled yet.";

    internal AllocationFeed(int processId)
        : base(processId, new EventPipeProvider(RuntimeProvider, GcKeyword, VerboseLevel), "its allocations")
    {
    }

    /// <summary>Most allocated first.</summary>
    public ObservableCollection<AllocatedType> Types { get; } = [];

    public bool HasTypes => Types.Count > 0;

    /// <summary>"About 412 MB since this page opened, 7.8 MB/s".</summary>
    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    /// <summary>The list as shown, as tab-separated text with a header line.</summary>
    internal string AsText() =>
        "Type\tAllocated bytes (sampled)" + Environment.NewLine
        + string.Concat(Types.Select(t => $"{t.FullName}\t{t.Bytes}{Environment.NewLine}"));

    private protected override void Reset()
    {
        _bytes.Clear();
        _total = 0;
        _since = DateTime.Now;
    }

    private protected override void Show()
    {
        List<AllocatedType> types;
        long total;
        DateTime since;
        lock (Gate)
        {
            total = _total;
            since = _since;
            types = _bytes
                .OrderByDescending(t => t.Value)
                .Select(t => new AllocatedType(t.Key, t.Value, _total))
                .ToList();
        }

        Types.Clear();
        foreach (var type in types)
            Types.Add(type);
        OnPropertyChanged(nameof(HasTypes));

        var seconds = Math.Max((DateTime.Now - since).TotalSeconds, 1);
        Summary = total == 0
            ? "Nothing sampled yet."
            : $"About {Format.Bytes(total)} allocated, {Format.BytesPerSecond(total / seconds)} on average";
    }

    /// <summary>
    /// GCAllocationTick: the amount since the last tick (32 and 64 bit), kind, CLR instance, then
    /// the type's id (a pointer) and name.
    /// </summary>
    private protected override void OnEvent(EventMetadata metadata, ReadOnlySpan<byte> payload, int pointerSize)
    {
        if (metadata.EventId != AllocationTickEvent)
            return;

        var reader = new SpanReader(payload);
        reader.Offset += sizeof(uint) + sizeof(uint) + sizeof(ushort);
        var amount = (long)reader.UInt64();
        reader.Offset += pointerSize;
        var type = reader.String();

        lock (Gate)
        {
            _bytes[type] = _bytes.GetValueOrDefault(type) + amount;
            _total += amount;
            Changed();
        }
    }
}
