using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>How installing a driver from a file went.</summary>
internal enum InfOutcome
{
    Installed,

    /// <summary>The INF has a driver for the device, but Windows ranks the one it has higher; forcing installs it anyway.</summary>
    NotBetter,

    /// <summary>The INF doesn't name any of the device's hardware or compatible ids.</summary>
    NoMatch,

    Failed,
}

/// <summary>
/// Installs driver packages from an INF file with newdev.dll, as Device Manager's "Browse my
/// computer" and pnputil do. Administrators only. Never shows Windows' own prompts: a package that
/// would need one (an unsigned driver) fails with a reason instead.
/// </summary>
internal static partial class DriverPackages
{
    private const uint InstallForce = 0x1;
    private const uint InstallNonInteractive = 0x4;

    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorCancelled = 1223;
    private const int ErrorNoSuchDevinst = unchecked((int)0xE000020B);
    private const int ErrorNoCatalogForOemInf = unchecked((int)0xE000022F);
    private const int ErrorInWow64 = unchecked((int)0xE0000235);
    private const int ErrorDriverStoreAddFailed = unchecked((int)0xE0000247);
    private const int ErrorNoSignature = unchecked((int)0x800B0100);
    private const int ErrorUntrustedRoot = unchecked((int)0x800B0109);

    /// <summary>
    /// Adds the package to the driver store and installs it on every connected device it's the best
    /// driver for. Returns why it failed (null on success) and whether a restart finishes it.
    /// </summary>
    public static (string? Failure, bool Restart) Install(string inf)
    {
        if (!DiInstallDriverW(0, inf, 0, out var reboot))
            return (Describe(Marshal.GetLastPInvokeError()), false);
        return (null, reboot != 0);
    }

    /// <summary>
    /// Installs the INF's driver on the devices with the first of <paramref name="ids"/> it names
    /// (most specific first). Without <paramref name="force"/>, only when Windows ranks it better than
    /// the driver the device has.
    /// </summary>
    public static (InfOutcome Outcome, string? Failure, bool Restart) InstallFor(string inf, IReadOnlyList<string> ids, bool force)
    {
        string text;
        try
        {
            text = File.ReadAllText(inf);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (InfOutcome.Failed, "The INF file can't be read.", false);
        }

        // An INF lists the ids it supports in its models sections: the most specific one named wins.
        var id = ids.FirstOrDefault(i => text.Contains(i, StringComparison.OrdinalIgnoreCase));
        if (id is null)
            return (InfOutcome.NoMatch, "This INF has no driver for this device: it names none of the device's hardware or compatible ids.", false);

        if (UpdateDriverForPlugAndPlayDevicesW(0, id, inf, InstallNonInteractive | (force ? InstallForce : 0), out var reboot))
            return (InfOutcome.Installed, null, reboot != 0);

        var error = Marshal.GetLastPInvokeError();
        return error == ErrorNoMoreItems && !force
            ? (InfOutcome.NotBetter, null, false)
            : (InfOutcome.Failed, Describe(error), false);
    }

    private static string Describe(int error) => error switch
    {
        ErrorFileNotFound => "The INF file, or a file it needs, isn't there.",
        ErrorAccessDenied => "Windows refused: installing drivers needs administrator rights.",
        ErrorCancelled => "The installation was cancelled.",
        ErrorNoSuchDevinst => "No connected device uses the hardware id this driver is for.",
        ErrorNoMoreItems => "Windows found nothing in the INF to install on this device.",
        ErrorNoCatalogForOemInf => "The package has no catalog file, so Windows can't check its signature.",
        ErrorInWow64 => "This driver can't be installed from a 32-bit process.",
        ErrorDriverStoreAddFailed => "Windows couldn't add the package to the driver store. It may be for another kind of Windows or processor.",
        ErrorNoSignature => "The driver isn't signed, and Windows only installs signed drivers.",
        ErrorUntrustedRoot => "The driver is signed with a certificate Windows doesn't trust.",
        _ => $"Windows couldn't install it (0x{error:X8}).",
    };

    [LibraryImport("newdev.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateDriverForPlugAndPlayDevicesW(nint parent, string hardwareId, string infPath, uint flags, out int reboot);

    [LibraryImport("newdev.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DiInstallDriverW(nint parent, string infPath, uint flags, out int reboot);
}
