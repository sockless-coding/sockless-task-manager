using System.Buffers.Binary;
using System.Text;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>What a kernel memory dump says about its crash.</summary>
/// <param name="Time">When Windows stopped, from the dump's header.</param>
/// <param name="Culprit">The driver file most likely at fault, or null when only Windows' own kernel was involved or the dump has no driver list.</param>
/// <param name="Blame">How <paramref name="Culprit"/> was found, in words.</param>
internal sealed record DumpInfo(string Path, DateTime? Time, uint Code, IReadOnlyList<ulong> Parameters, string? Culprit, string? Blame);

/// <summary>
/// Reads Windows kernel dumps (minidumps, MEMORY.DMP and live kernel reports) without a debugger.
/// The 64-bit header gives the stop code and its parameters. A minidump also keeps the list of
/// loaded drivers and the crashing thread's stack, so the driver blamed is the one a stop code's
/// address parameter points into, else the first driver other than the kernel found on the stack,
/// the way BlueScreenView guesses. The layouts are read by offset and every candidate checked, so
/// a dump in another layout gives its stop code and no blame rather than a wrong one.
/// </summary>
internal static class KernelDump
{
    private const int HeaderSize = 0x2000;
    private const int BugCheckCode = 0x38;
    private const int BugCheckParameters = 0x40;
    private const int DumpType = 0xF98;
    private const int SystemTime = 0xFA8;
    private const uint TriageDump = 4;

    /// <summary>A minidump is a few MB at most; anything bigger is a full dump, read for its header only.</summary>
    private const long TriageMaxBytes = 32L << 20;

    // TRIAGE_DUMP64, at the end of the header.
    private const int CallStackOffset = 0x28;
    private const int SizeOfCallStack = 0x2C;
    private const int DriverListOffset = 0x30;
    private const int DriverCount = 0x34;

    // DUMP_DRIVER_ENTRY64: the name's offset, then KLDR_DATA_TABLE_ENTRY64 (DllBase 0x30, SizeOfImage 0x40) 8-aligned.
    private const int LdrEntry = 8;
    private const int DllBase = 0x30;
    private const int SizeOfImage = 0x40;
    private static readonly int[] EntrySizes = [0x90, 0x98, 0xA0, 0xA8, 0x88];

    /// <summary>Windows' own kernel and HAL: on every stack, never the useful answer.</summary>
    private static readonly HashSet<string> Kernel = new(StringComparer.OrdinalIgnoreCase)
    {
        "ntoskrnl.exe", "ntkrnlmp.exe", "ntkrnlpa.exe", "ntkrpamp.exe", "ntkrla57.exe", "hal.dll", "halmacpi.dll", "kd.dll",
    };

    public static DumpInfo? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[HeaderSize];
            if (stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) < HeaderSize)
                return null;

            // "PAGE" "DU64": the 64-bit kernel dump header.
            if (!header.AsSpan(0, 8).SequenceEqual("PAGEDU64"u8))
                return null;

            var span = header.AsSpan();
            var code = BinaryPrimitives.ReadUInt32LittleEndian(span[BugCheckCode..]);
            var parameters = new ulong[4];
            for (var i = 0; i < 4; i++)
                parameters[i] = BinaryPrimitives.ReadUInt64LittleEndian(span[(BugCheckParameters + i * 8)..]);

            var ticks = BinaryPrimitives.ReadInt64LittleEndian(span[SystemTime..]);
            DateTime? time = ticks > 0 && ticks < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(ticks) : null;

            string? culprit = null, blame = null;
            if (BinaryPrimitives.ReadUInt32LittleEndian(span[DumpType..]) == TriageDump && stream.Length <= TriageMaxBytes)
            {
                var all = new byte[stream.Length];
                header.CopyTo(all, 0);
                stream.ReadAtLeast(all.AsSpan(HeaderSize), all.Length - HeaderSize, throwOnEndOfStream: false);
                (culprit, blame) = Blame(all, parameters);
            }

            return new DumpInfo(path, time, code, parameters, culprit, blame);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (string? Culprit, string? Blame) Blame(byte[] dump, ulong[] parameters)
    {
        if (Drivers(dump) is not { } drivers)
            return (null, null);

        foreach (var parameter in parameters)
        {
            if (Owner(drivers, parameter) is { } owner && !Kernel.Contains(owner))
                return (owner, "A stop code parameter points into it");
        }

        // The crashing thread's stack, as saved in the minidump: return addresses into drivers.
        var span = dump.AsSpan();
        var stack = BinaryPrimitives.ReadUInt32LittleEndian(span[(HeaderSize + CallStackOffset)..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(span[(HeaderSize + SizeOfCallStack)..]);
        foreach (var start in new[] { (long)stack, HeaderSize + (long)stack })
        {
            if (size == 0 || size > 1 << 20 || start + size > dump.Length)
                continue;

            for (var at = start; at + 8 <= start + size; at += 8)
            {
                var value = BinaryPrimitives.ReadUInt64LittleEndian(span[(int)at..]);
                if (Owner(drivers, value) is { } owner && !Kernel.Contains(owner))
                    return (owner, "Its code is on the crashing thread's stack");
            }
        }

        return (null, "Only Windows' own kernel is on the crashing thread's stack");
    }

    private static string? Owner(List<(string Name, ulong Base, uint Size)> drivers, ulong address)
    {
        if (address < 0xFFFF800000000000)
            return null;

        foreach (var (name, start, size) in drivers)
        {
            if (address >= start && address < start + size)
                return name;
        }

        return null;
    }

    /// <summary>
    /// The minidump's list of loaded drivers. Offsets may be from the start of the file or of the
    /// triage block, and the entry size varies by Windows version: the first reading in which every
    /// entry is a plausible driver (a kernel address, a sane size, a .sys/.dll/.exe name) wins.
    /// </summary>
    private static List<(string Name, ulong Base, uint Size)>? Drivers(byte[] dump)
    {
        var span = dump.AsSpan();
        var listOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[(HeaderSize + DriverListOffset)..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(span[(HeaderSize + DriverCount)..]);
        if (count is 0 or > 4096)
            return null;

        foreach (var origin in new long[] { 0, HeaderSize })
        {
            foreach (var entrySize in EntrySizes)
            {
                var drivers = new List<(string, ulong, uint)>((int)count);
                var plausible = true;
                for (var i = 0; i < count && plausible; i++)
                {
                    var entry = origin + listOffset + (long)i * entrySize;
                    if (entry < 0 || entry + LdrEntry + SizeOfImage + 4 > dump.Length)
                    {
                        plausible = false;
                        break;
                    }

                    var e = span[(int)entry..];
                    var nameAt = origin + BinaryPrimitives.ReadUInt32LittleEndian(e);
                    var start = BinaryPrimitives.ReadUInt64LittleEndian(e[(LdrEntry + DllBase)..]);
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(e[(LdrEntry + SizeOfImage)..]);
                    var name = DumpString(dump, nameAt);
                    plausible = name is not null && start >= 0xFFFF800000000000 && size is > 0 and < 1u << 30;
                    if (plausible)
                        drivers.Add((Path.GetFileName(name!), start, size));
                }

                if (plausible && drivers.Any(d => Kernel.Contains(d.Item1)))
                    return drivers;
            }
        }

        return null;
    }

    /// <summary>DUMP_STRING: a character count, then UTF-16 text. Null unless it reads as a driver file name.</summary>
    private static string? DumpString(byte[] dump, long at)
    {
        if (at < 0 || at + 4 > dump.Length)
            return null;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(dump.AsSpan((int)at));
        if (length is 0 or > 260 || at + 4 + length * 2 > dump.Length)
            return null;

        var name = Encoding.Unicode.GetString(dump, (int)at + 4, (int)length * 2).TrimEnd('\0');
        return name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name
            : null;
    }
}
