using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Core.Interop;

/// <summary>
/// A thin wrapper over a Performance Data Helper query. Counters are added by their English
/// path (<c>\Processor Information(_Total)\% Processor Utility</c>) so they work on any
/// Windows display language. Rate counters need two collections before they return values.
/// </summary>
public sealed partial class PdhQuery : IDisposable
{
    private nint _handle;

    public PdhQuery()
    {
        Check(PdhOpenQuery(null, 0, out _handle), "PdhOpenQuery");
    }

    /// <summary>Adds a counter, or returns null if this machine doesn't expose it.</summary>
    public PdhCounter? TryAdd(string englishPath)
    {
        return PdhAddEnglishCounter(_handle, englishPath, 0, out var counter) == 0
            ? new PdhCounter(counter)
            : null;
    }

    public bool Collect() => PdhCollectQueryData(_handle) == 0;

    public void Dispose()
    {
        if (_handle != 0)
        {
            PdhCloseQuery(_handle);
            _handle = 0;
        }
    }

    private static void Check(uint status, string call)
    {
        if (status != 0)
            throw new InvalidOperationException($"{call} failed with 0x{status:X8}");
    }

    internal const uint PdhMoreData = 0x800007D2;
    internal const uint FormatDouble = 0x00000200;
    internal const uint FormatNoCap100 = 0x00008000;

    [StructLayout(LayoutKind.Explicit)]
    internal struct FormattedValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FormattedValueItem
    {
        public nint Name;
        public FormattedValue Value;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounter(nint query, string fullCounterPath, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);

    [LibraryImport("pdh.dll")]
    internal static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out FormattedValue value);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    internal static unsafe partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, void* items);
}

/// <summary>A counter in a <see cref="PdhQuery"/>. Read after <see cref="PdhQuery.Collect"/>.</summary>
public sealed class PdhCounter
{
    private readonly nint _handle;
    private byte[] _buffer = new byte[4096];

    internal PdhCounter(nint handle) => _handle = handle;

    /// <summary>The current value, or null until the counter has enough data.</summary>
    public double? Value
    {
        get
        {
            var status = PdhQuery.PdhGetFormattedCounterValue(
                _handle, PdhQuery.FormatDouble | PdhQuery.FormatNoCap100, out _, out var value);
            return status == 0 && value.CStatus <= 1 ? value.DoubleValue : null;
        }
    }

    /// <summary>
    /// Reads every instance of a wildcard counter (<c>\PhysicalDisk(*)\% Idle Time</c>) into
    /// <paramref name="into"/>. Instances that share a name are summed.
    /// </summary>
    public void ReadInstances(Dictionary<string, double> into) => ReadInstances(into, includeInvalid: false);

    /// <summary>Instance names only. Works after a single collect, before rates have values.</summary>
    public IReadOnlyCollection<string> ReadInstanceNames()
    {
        var all = new Dictionary<string, double>();
        ReadInstances(all, includeInvalid: true);
        return all.Keys;
    }

    private unsafe void ReadInstances(Dictionary<string, double> into, bool includeInvalid)
    {
        into.Clear();

        while (true)
        {
            var size = (uint)_buffer.Length;
            uint count;
            uint status;
            fixed (byte* buffer = _buffer)
            {
                status = PdhQuery.PdhGetFormattedCounterArray(
                    _handle, PdhQuery.FormatDouble | PdhQuery.FormatNoCap100, ref size, out count, buffer);

                if (status == 0)
                {
                    var items = (PdhQuery.FormattedValueItem*)buffer;
                    for (var i = 0; i < count; i++)
                    {
                        var valid = items[i].Value.CStatus <= 1;
                        if (!valid && !includeInvalid)
                            continue;

                        var name = Marshal.PtrToStringUni(items[i].Name) ?? string.Empty;
                        into[name] = into.GetValueOrDefault(name) + (valid ? items[i].Value.DoubleValue : 0);
                    }

                    return;
                }
            }

            if (status != PdhQuery.PdhMoreData)
                return;

            _buffer = new byte[Math.Max(size, (uint)_buffer.Length * 2)];
        }
    }
}
