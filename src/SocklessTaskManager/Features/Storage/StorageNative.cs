using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Storage;

/// <summary>Directory listing and the backup privilege, for the disk scan.</summary>
internal static unsafe partial class StorageNative
{
    private const uint FileListDirectory = 0x0001;
    private const uint ShareReadWriteDelete = 0x0007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const int FileFullDirectoryInfo = 14;

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint PrivilegeEnabled = 0x0002;

    private static int _backupEnabled;

    /// <summary>
    /// Elevated, the backup privilege lets a directory opened with backup semantics be listed
    /// whatever its ACL says (System Volume Information, other users' profiles). It only has an
    /// effect on handles opened that way, so enabling it for the process is harmless elsewhere.
    /// </summary>
    public static void EnableBackupPrivilege()
    {
        if (!Elevation.IsElevated || Interlocked.Exchange(ref _backupEnabled, 1) == 1)
            return;

        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            return;

        try
        {
            if (!LookupPrivilegeValue(null, "SeBackupPrivilege", out var luid))
                return;

            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = PrivilegeEnabled };
            AdjustTokenPrivileges(token, false, &privileges, 0, null, null);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>Opens a directory for listing; -1 if it can't be read.</summary>
    public static nint OpenDirectory(string path)
    {
        // The \\?\ prefix lifts the 260-character limit; deep folders (node_modules) need it.
        var full = path.StartsWith(@"\\", StringComparison.Ordinal) ? path : @"\\?\" + path;
        return CreateFile(full, FileListDirectory, ShareReadWriteDelete, 0, OpenExisting, BackupSemantics, 0);
    }

    /// <summary>Fills <paramref name="buffer"/> with the next batch of FILE_FULL_DIR_INFO entries; false when there are no more.</summary>
    public static bool ReadDirectory(nint handle, Span<byte> buffer)
    {
        fixed (byte* p = buffer)
            return GetFileInformationByHandleEx(handle, FileFullDirectoryInfo, p, (uint)buffer.Length);
    }

    public static void Close(nint handle) => CloseHandle(handle);

    private const uint FileOperationDelete = 3;
    private const ushort NoConfirmation = 0x0010;
    private const ushort AllowUndo = 0x0040;
    private const ushort NoErrorUi = 0x0400;
    private const ushort WantNukeWarning = 0x4000;
    private const uint ObjectPropertiesFilePath = 0x2;

    /// <summary>
    /// Deletes a file or folder through the shell, as Explorer does: to the Recycle Bin unless
    /// <paramref name="permanently"/>, with Windows' progress window for long deletes. The app has
    /// already asked, so the shell doesn't; but if something can't be recycled (too large, a drive
    /// without a bin) the shell asks before deleting it for good. Errors come back as the result,
    /// not as dialogs. Runs on its own STA thread, which the shell's COM objects need.
    /// </summary>
    /// <returns>0 on success; otherwise a shell error code. <paramref name="cancelled"/> is set if the user stopped it.</returns>
    public static Task<(int Result, bool Cancelled)> DeleteAsync(string path, bool permanently, nint owner)
    {
        var done = new TaskCompletionSource<(int, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(Delete(path, permanently, owner));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Storage delete",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static (int Result, bool Cancelled) Delete(string path, bool permanently, nint owner)
    {
        // pFrom is a list ending in an empty string: the path, then a second terminator.
        fixed (char* from = path + "\0")
        {
            var operation = new FileOperation
            {
                Owner = owner,
                Function = FileOperationDelete,
                From = from,
                Flags = (ushort)(NoConfirmation | NoErrorUi | (permanently ? 0 : AllowUndo | WantNukeWarning)),
            };
            var result = SHFileOperation(&operation);
            return (result, operation.AnyOperationsAborted != 0);
        }
    }

    /// <summary>The shell's Properties window for a file or folder, as in Explorer.</summary>
    public static bool ShowProperties(string path, nint owner)
    {
        fixed (char* name = path)
            return SHObjectProperties(owner, ObjectPropertiesFilePath, name, null);
    }

    /// <summary>SHFILEOPSTRUCTW, with natural (x64/ARM64) alignment.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileOperation
    {
        public nint Owner;
        public uint Function;
        public char* From;
        public char* To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public char* ProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation(FileOperation* operation);

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHObjectProperties(nint owner, uint type, char* name, char* page);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(nint handle, int infoClass, void* buffer, uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, TokenPrivileges* state, uint length, TokenPrivileges* previous, uint* returned);
}

/// <summary>
/// The entries of one directory, read in 64 KB batches of FILE_FULL_DIR_INFO and parsed by
/// offset. Unlike FindFirstFile this reports each file's allocation size, its size on disk.
/// </summary>
internal ref struct DirectoryListing
{
    // FILE_FULL_DIR_INFO
    private const int NextEntryOffset = 0;
    private const int AllocationSizeOffset = 48;
    private const int AttributesOffset = 56;
    private const int NameLengthOffset = 60;
    private const int NameOffset = 68;

    private const uint DirectoryAttribute = 0x10;
    private const uint ReparsePointAttribute = 0x400;

    private readonly nint _handle;
    private readonly Span<byte> _buffer;
    private int _next;
    private bool _done;

    private DirectoryListing(nint handle, Span<byte> buffer)
    {
        _handle = handle;
        _buffer = buffer;
        _next = -1;
    }

    public static bool TryOpen(string path, Span<byte> buffer, out DirectoryListing listing)
    {
        var handle = StorageNative.OpenDirectory(path);
        if (handle == -1)
        {
            listing = default;
            return false;
        }

        listing = new DirectoryListing(handle, buffer);
        return true;
    }

    public ReadOnlySpan<char> Name { get; private set; }

    public long AllocatedBytes { get; private set; }

    public uint Attributes { get; private set; }

    public readonly bool IsDirectory => (Attributes & DirectoryAttribute) != 0;

    /// <summary>A junction, symbolic link or mount point; for a directory, its target is counted where it lives.</summary>
    public readonly bool IsReparsePoint => (Attributes & ReparsePointAttribute) != 0;

    public bool MoveNext()
    {
        while (true)
        {
            int offset;
            if (_next < 0)
            {
                if (_done || !StorageNative.ReadDirectory(_handle, _buffer))
                {
                    _done = true;
                    return false;
                }

                offset = 0;
            }
            else
            {
                offset = _next;
            }

            var entry = _buffer[offset..];
            var nextOffset = BinaryPrimitives.ReadInt32LittleEndian(entry[NextEntryOffset..]);
            _next = nextOffset == 0 ? -1 : offset + nextOffset;

            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(entry[NameLengthOffset..]);
            var name = MemoryMarshal.Cast<byte, char>(entry.Slice(NameOffset, nameLength));
            if (name is "." or "..")
                continue;

            Name = name;
            AllocatedBytes = BinaryPrimitives.ReadInt64LittleEndian(entry[AllocationSizeOffset..]);
            Attributes = BinaryPrimitives.ReadUInt32LittleEndian(entry[AttributesOffset..]);
            return true;
        }
    }

    public readonly void Dispose()
    {
        if (_handle is not 0 and not -1)
            StorageNative.Close(_handle);
    }
}
