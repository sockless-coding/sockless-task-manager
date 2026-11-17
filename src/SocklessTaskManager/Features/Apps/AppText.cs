using System.Globalization;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Apps;

/// <summary>How apps read on the page.</summary>
internal static class AppText
{
    public static string GroupTitle(AppGroup group) => group switch
    {
        AppGroup.Programs => "Desktop programs",
        AppGroup.Packaged => "Store and packaged apps",
        AppGroup.PackageManagers => "Package managers",
        AppGroup.Features => "Optional features",
        AppGroup.Components => "Frameworks and components",
        _ => "Updates",
    };

    public static string GroupAbout(AppGroup group) => group switch
    {
        AppGroup.Programs => "Installed by a setup program or Windows Installer: what Programs and Features lists",
        AppGroup.Packaged => "MSIX packages, from the Microsoft Store or a package file",
        AppGroup.PackageManagers => "Installed from the command line by winget (portable apps), Scoop or Chocolatey",
        AppGroup.Features => "Added to Windows itself: optional features and Windows features",
        AppGroup.Components => "Frameworks, runtimes and parts of other apps, which Windows doesn't list on their own",
        _ => "Updates to installed programs",
    };

    /// <summary>Hidden unless components are asked for: they come and go with what they belong to.</summary>
    public static bool IsComponent(AppItem item) => item.Group is AppGroup.Components or AppGroup.Updates;

    public static string Date(DateTime? date) => date is { } value ? value.ToString("d", CultureInfo.CurrentCulture) : "—";

    public static string Size(long? bytes) => bytes is { } value ? Format.Bytes(value) : "—";

    public static string Scope(AppItem item) => item.Group == AppGroup.Features
        ? "Windows, for everyone"
        : item.IsPerUser ? "You only" : "Everyone on this computer";

    /// <summary>
    /// Versions compared part by part, numbers as numbers ("10.2" after "9.15"); text parts as
    /// text. Missing versions sort last.
    /// </summary>
    public static int CompareVersions(string? a, string? b)
    {
        if (a is null || b is null)
            return a is null ? (b is null ? 0 : 1) : -1;

        var left = a.Split('.', '-', '+', ' ', '_');
        var right = b.Split('.', '-', '+', ' ', '_');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            if (i >= left.Length)
                return -1;
            if (i >= right.Length)
                return 1;

            var result = long.TryParse(left[i], out var x) && long.TryParse(right[i], out var y)
                ? x.CompareTo(y)
                : string.Compare(left[i], right[i], StringComparison.OrdinalIgnoreCase);
            if (result != 0)
                return result;
        }

        return 0;
    }
}
