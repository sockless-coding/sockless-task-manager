using System.Reflection;

namespace Sockless.TaskManager.Features.Updates;

/// <summary>
/// This build's version and how it compares with a release tag. Releases are tagged year.month.rev
/// (2026.10.1, or 2026.10.1-beta for a prerelease) and the release workflow builds with that version.
/// Local and CI builds are 0.0.0 (the csproj default) and never look for updates.
/// </summary>
internal static class ReleaseVersion
{
    public static Version Current { get; } = Trim(typeof(ReleaseVersion).Assembly.GetName().Version ?? new Version());

    public static bool IsRelease => Current.Major > 0;

    /// <summary>The assembly version drops "-beta", so the informational version says whether this is a prerelease.</summary>
    private static readonly bool IsPrerelease =
        typeof(ReleaseVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0].Contains('-') == true;

    public static string Text => IsRelease ? $"Version {Current}{(IsPrerelease ? " (prerelease)" : string.Empty)}" : "Development build";

    /// <summary>True if <paramref name="tag"/> is later than this build, or is the release of this prerelease.</summary>
    public static bool IsNewer(string tag)
    {
        if (!Version.TryParse(tag.Split('-')[0], out var version))
            return false;

        version = Trim(version);
        return version > Current || (version == Current && IsPrerelease && !tag.Contains('-'));
    }

    // The assembly version has four parts and a tag three; 2026.10.1.0 must equal 2026.10.1.
    private static Version Trim(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
