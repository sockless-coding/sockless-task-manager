using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Core.Interop;

/// <summary>
/// <c>NtQuerySystemInformation</c>, the call behind the Memory and Processes readings. Every class used
/// here works without elevation. Layouts are the 64-bit ones (the app ships for x64), and
/// buffers are parsed by offset.
/// </summary>
internal static partial class NtSystem
{
    public const int ProcessInformation = 5;
    public const int ModuleInformation = 11;
    public const int PoolTagInformation = 22;
    public const int MemoryListInformation = 80;

    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const uint StatusBufferTooSmall = 0xC0000023;

    public static bool IsSupported => Environment.Is64BitProcess;

    /// <summary>
    /// A buffer on the pinned heap. Some classes return absolute pointers into the buffer
    /// (process image names), so it must not move between the call and the parse.
    /// </summary>
    public static byte[] Buffer(int size) => GC.AllocateUninitializedArray<byte>(size, pinned: true);

    /// <summary>Fills <paramref name="buffer"/>, growing it as needed. Returns the bytes written, or 0 on failure.</summary>
    public static unsafe int Query(int infoClass, ref byte[] buffer)
    {
        if (!IsSupported)
            return 0;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            uint status, returned;
            fixed (byte* p = buffer)
                status = NtQuerySystemInformation(infoClass, p, (uint)buffer.Length, out returned);

            if (status == 0)
                return (int)returned;
            if (status is not StatusInfoLengthMismatch and not StatusBufferTooSmall)
                return 0;

            // Lists grow between calls, so leave headroom.
            buffer = Buffer((int)Math.Max(returned + returned / 4 + 4096, (uint)buffer.Length * 2));
        }

        return 0;
    }

    /// <summary>Offset of an absolute pointer into a pinned <paramref name="buffer"/>, or -1 if it points elsewhere.</summary>
    public static unsafe int OffsetOf(byte[] buffer, int length, nint pointer)
    {
        fixed (byte* p = buffer)
        {
            var offset = (long)pointer - (long)p;
            return offset >= 0 && offset < length ? (int)offset : -1;
        }
    }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial uint NtQuerySystemInformation(int infoClass, void* buffer, uint length, out uint returnLength);
}
