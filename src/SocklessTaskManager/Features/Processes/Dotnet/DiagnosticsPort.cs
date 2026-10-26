using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Sockless.TaskManager.Features.Processes.Dotnet;

/// <summary>
/// The diagnostics port a .NET (Core) process opens: the named pipe dotnet-counters, dotnet-trace
/// and dotnet-gcdump talk to. One command per connection; a tracing command keeps its connection
/// and streams the session's events on it. Protocol: dotnet/diagnostics, ipc-protocol.md.
/// Commands to a process go one at a time: starting and stopping sessions at the same moment
/// can hang the runtime's diagnostics server for good (seen on .NET 10), and with it every tool.
/// </summary>
internal static partial class DiagnosticsPort
{
    private const int HeaderSize = 20;
    private const byte EventPipeCommands = 0x02;
    private const byte StopTracingCommand = 0x01;
    private const byte CollectTracing2Command = 0x03;
    private const byte ProcessCommands = 0x04;
    private const byte ProcessInfo2Command = 0x04;
    private const byte ServerCommands = 0xFF;
    private const byte ErrorResponse = 0xFF;
    private const uint NetTraceFormat = 1;
    private const int ErrorFileNotFound = 2;

    /// <summary>The runtime doesn't know the command: older than the one that added it.</summary>
    internal const uint UnknownCommand = 0x80131384;

    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Gates = new();

    private static ReadOnlySpan<byte> Magic => "DOTNET_IPC_V1\0"u8;

    /// <summary>
    /// Every process with a diagnostics port, from one listing of the pipes: a millisecond or so,
    /// where asking each process would take one call per process.
    /// </summary>
    internal static HashSet<int> ListProcesses()
    {
        const string prefix = "dotnet-diagnostic-";
        var ids = new HashSet<int>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(@"\\.\pipe\", prefix + "*"))
            {
                var name = path.AsSpan(path.LastIndexOf('\\') + 1);
                if (name.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(name[prefix.Length..], out var id))
                    ids.Add(id);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return ids;
    }

    /// <summary>Whether the process has a diagnostics port. Asks Windows about the pipe without connecting to it.</summary>
    internal static bool Exists(int processId) =>
        WaitNamedPipe($@"\\.\pipe\{PipeName(processId)}", 1) || Marshal.GetLastPInvokeError() != ErrorFileNotFound;

    /// <summary>The runtime's version, "9.0.3"; null before .NET 6, which can't say.</summary>
    internal static async Task<string?> RuntimeVersionAsync(int processId, CancellationToken cancel)
    {
        using var turn = await TakeTurnAsync(processId, cancel);
        await using var pipe = await ConnectAsync(processId, cancel);
        await pipe.WriteAsync(Message(ProcessCommands, ProcessInfo2Command, []), cancel);

        byte[] payload;
        try
        {
            payload = await ReadResponseAsync(pipe, cancel);
        }
        catch (DiagnosticsException e) when (e.Code == UnknownCommand)
        {
            return null;
        }

        // Process id, runtime cookie, then command line, OS, architecture, entry assembly, runtime version.
        var offset = sizeof(ulong) + 16;
        string? version = null;
        for (var i = 0; i < 5; i++)
            version = ReadString(payload, ref offset);
        return version?.Split('+')[0];
    }

    /// <summary>Starts an EventPipe session with one provider.</summary>
    internal static Task<EventPipeSession> StartAsync(int processId, string provider, ulong keywords, uint level, string? filter,
        uint bufferMegabytes, CancellationToken cancel) =>
        StartAsync(processId, [new EventPipeProvider(provider, keywords, level, filter)], bufferMegabytes, cancel);

    /// <summary>
    /// Starts an EventPipe session. The returned session's stream carries its events in the
    /// nettrace format until it's disposed, which stops it.
    /// </summary>
    internal static async Task<EventPipeSession> StartAsync(int processId, IReadOnlyList<EventPipeProvider> providers,
        uint bufferMegabytes, CancellationToken cancel)
    {
        var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(bufferMegabytes);  // most it may hold before dropping events
            writer.Write(NetTraceFormat);
            writer.Write((byte)0);          // no rundown: method and module lists aren't needed
            writer.Write((uint)providers.Count);
            foreach (var provider in providers)
            {
                writer.Write(provider.Keywords);
                writer.Write(provider.Level);
                WriteString(writer, provider.Name);
                WriteString(writer, provider.Filter);
            }
        }

        using var turn = await TakeTurnAsync(processId, cancel);
        var pipe = await ConnectAsync(processId, cancel);
        try
        {
            await pipe.WriteAsync(Message(EventPipeCommands, CollectTracing2Command, body.ToArray()), cancel);
            var payload = await ReadResponseAsync(pipe, cancel);
            return new EventPipeSession(processId, BinaryPrimitives.ReadUInt64LittleEndian(payload), pipe);
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    internal static async Task StopAsync(int processId, ulong sessionId, CancellationToken cancel)
    {
        var body = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(body, sessionId);

        using var turn = await TakeTurnAsync(processId, cancel);
        await using var pipe = await ConnectAsync(processId, cancel);
        await pipe.WriteAsync(Message(EventPipeCommands, StopTracingCommand, body), cancel);
        await ReadResponseAsync(pipe, cancel);
    }

    private static string PipeName(int processId) => $"dotnet-diagnostic-{processId}";

    /// <summary>Waits for this process's other commands to finish. Dispose to let the next one go.</summary>
    private static async Task<Turn> TakeTurnAsync(int processId, CancellationToken cancel)
    {
        var gate = Gates.GetOrAdd(processId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancel);
        return new Turn(gate);
    }

    private sealed class Turn(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(int processId, CancellationToken cancel)
    {
        var pipe = new NamedPipeClientStream(".", PipeName(processId), PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        try
        {
            await pipe.ConnectAsync(cancel);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    private static byte[] Message(byte commandSet, byte command, ReadOnlySpan<byte> body)
    {
        var message = new byte[HeaderSize + body.Length];
        Magic.CopyTo(message);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(14), (ushort)message.Length);
        message[16] = commandSet;
        message[17] = command;
        body.CopyTo(message.AsSpan(HeaderSize));
        return message;
    }

    /// <summary>A response's payload; an error response throws.</summary>
    private static async Task<byte[]> ReadResponseAsync(Stream pipe, CancellationToken cancel)
    {
        var header = new byte[HeaderSize];
        await pipe.ReadExactlyAsync(header, cancel);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Not a diagnostics IPC response.");

        var size = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
        if (size < HeaderSize)
            throw new InvalidDataException("Diagnostics IPC response too short.");

        var payload = new byte[size - HeaderSize];
        await pipe.ReadExactlyAsync(payload, cancel);
        if (header[16] == ServerCommands && header[17] == ErrorResponse)
            throw new DiagnosticsException(payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0);
        return payload;
    }

    /// <summary>Length in characters including the terminator, then UTF-16; null is length 0.</summary>
    private static void WriteString(BinaryWriter writer, string? value)
    {
        if (value is null)
        {
            writer.Write(0u);
            return;
        }

        writer.Write((uint)value.Length + 1);
        writer.Write(Encoding.Unicode.GetBytes(value + '\0'));
    }

    private static string? ReadString(byte[] buffer, ref int offset)
    {
        if (offset + 4 > buffer.Length)
            return null;
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
        offset += 4;
        if (length == 0 || offset + length * 2 > buffer.Length)
            return null;
        var value = Encoding.Unicode.GetString(buffer, offset, (length - 1) * 2);
        offset += length * 2;
        return value;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitNamedPipe(string name, uint timeout);
}

/// <summary>An event source to turn on in a session, and how much of it.</summary>
/// <param name="Filter">Provider arguments, "EventCounterIntervalSec=1"; null for none.</param>
internal sealed record EventPipeProvider(string Name, ulong Keywords, uint Level, string? Filter = null);

/// <summary>The runtime answered with an error: an HRESULT.</summary>
internal sealed class DiagnosticsException(uint code) : Exception($"The .NET runtime refused the request (0x{code:X8}).")
{
    public uint Code { get; } = code;
}

/// <summary>A running EventPipe session: its event stream. Disposing stops it.</summary>
internal sealed class EventPipeSession(int processId, ulong id, Stream stream) : IAsyncDisposable
{
    private bool _stopped;

    public Stream Stream { get; } = stream;

    /// <summary>
    /// Asks the runtime to stop: it sends what it still holds, then ends the stream. Only while
    /// the stream is still read: the runtime's stop waits for its writes, and a full pipe nobody
    /// reads would hold it (and the diagnostics server) until the timeout. It may already be gone.
    /// </summary>
    public async Task StopAsync()
    {
        if (_stopped)
            return;
        _stopped = true;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await DiagnosticsPort.StopAsync(processId, id, timeout.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException or DiagnosticsException or InvalidDataException)
        {
        }
    }

    /// <summary>
    /// Closes the stream first, which fails any write the runtime is stuck in, then makes sure
    /// the session is stopped.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        await StopAsync();
    }
}
