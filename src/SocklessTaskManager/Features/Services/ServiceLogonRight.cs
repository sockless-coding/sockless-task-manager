using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Services;

/// <summary>
/// SeServiceLogonRight, through the LSA. A service set to run as someone's account won't start
/// (error 1069) unless the account holds it, and <c>ChangeServiceConfigW</c> doesn't give it; the
/// Services console does, so this does too. Granting it again is harmless. Needs elevation.
/// </summary>
internal static unsafe partial class ServiceLogonRight
{
    private const string Right = "SeServiceLogonRight";
    private const uint PolicyCreateAccount = 0x0010;
    private const uint PolicyLookupNames = 0x0800;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>Null when the account holds the right (or needs none); otherwise why not.</summary>
    public static string? Grant(string account)
    {
        if (!NeedsRight(account))
            return null;

        // LookupAccountNameW doesn't know ".": that's this computer.
        var name = account.StartsWith(@".\", StringComparison.Ordinal) ? $@"{Environment.MachineName}\{account[2..]}" : account;
        if (Sid(name) is not { } sid)
            return $"Windows doesn't know the account {account}.";

        var attributes = default(ObjectAttributes);
        attributes.Length = (uint)sizeof(ObjectAttributes);
        var status = LsaOpenPolicy(null, &attributes, PolicyCreateAccount | PolicyLookupNames, out var policy);
        if (status != 0)
            return ServiceManager.Failure((int)LsaNtStatusToWinError(status));

        try
        {
            fixed (char* right = Right)
            fixed (byte* s = sid)
            {
                var rights = new UnicodeString
                {
                    Length = (ushort)(Right.Length * 2),
                    MaximumLength = (ushort)(Right.Length * 2),
                    Buffer = right,
                };
                status = LsaAddAccountRights(policy, s, &rights, 1);
                return status == 0 ? null : ServiceManager.Failure((int)LsaNtStatusToWinError(status));
            }
        }
        finally
        {
            LsaClose(policy);
        }
    }

    /// <summary>Windows' own accounts and services' virtual accounts log on as services already.</summary>
    private static bool NeedsRight(string account) =>
        account.Length > 0
        && !account.Equals(ServiceEditor.LocalSystem, StringComparison.OrdinalIgnoreCase)
        && !account.Equals(@".\LocalSystem", StringComparison.OrdinalIgnoreCase)
        && !account.StartsWith(@"NT AUTHORITY\", StringComparison.OrdinalIgnoreCase)
        && !account.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase);

    private static byte[]? Sid(string name)
    {
        uint sidSize = 0, domainSize = 0;
        LookupAccountName(null, name, null, ref sidSize, null, ref domainSize, out _);
        if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer || sidSize == 0)
            return null;

        var sid = new byte[sidSize];
        var domain = new char[Math.Max(domainSize, 1)];
        fixed (byte* s = sid)
        fixed (char* d = domain)
            return LookupAccountName(null, name, s, ref sidSize, d, ref domainSize, out _) ? sid : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    /// <summary>LSA_OBJECT_ATTRIBUTES: unused, but it must be there and zeroed, with its length.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public nint ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "LookupAccountNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupAccountName(string? system, string name, byte* sid, ref uint sidSize, char* domain, ref uint domainSize, out int use);

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaOpenPolicy(UnicodeString* system, ObjectAttributes* attributes, uint access, out nint policy);

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaAddAccountRights(nint policy, byte* sid, UnicodeString* rights, uint count);

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaClose(nint handle);

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaNtStatusToWinError(uint status);
}
