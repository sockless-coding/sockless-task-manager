using System.Runtime.InteropServices;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Apps;

/// <summary>
/// What's been added to Windows itself: optional features (capabilities, Settings → Optional
/// features) and Windows features (Turn Windows features on or off), through the DISM API. Only an
/// administrator can open DISM, so without elevation there's nothing here. DISM loads a lot, so a
/// session is opened for each scan or change and shut down after, one at a time.
/// </summary>
internal static unsafe partial class OptionalFeatures
{
    private const string OnlineImage = "DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}";
    private const int LogErrors = 0;
    private const int PackageNone = 0;
    private const int ErrorSuccessRestartRequired = 3010;

    // DismPackageFeatureState.
    private const int StateUninstallPending = 1;
    private const int StateInstalled = 4;
    private const int StateInstallPending = 5;

    // DismCapability / DismFeature: PCWSTR Name; DismPackageFeatureState State. Packed: 12 bytes each.
    private const int ItemSize = 12;
    private const int NameOffset = 0;
    private const int StateOffset = 8;

    // DismCapabilityInfo and DismFeatureInfo share their start: Name, State, DisplayName, Description, then
    // DownloadSize, InstallSize (capabilities) or RestartRequired (features).
    private const int DisplayNameOffset = 12;
    private const int DescriptionOffset = 20;
    private const int InstallSizeOffset = 32;
    private const int RestartOffset = 28;

    private static readonly Lock Gate = new();

    public static List<AppItem> Scan()
    {
        if (!Elevation.IsElevated)
            return [];

        var items = new List<AppItem>();
        WithSession(session =>
        {
            Capabilities(session, items);
            Features(session, items);
            return 0;
        });
        return items;
    }

    public static AppOutcome RemoveCapability(string name) =>
        Outcome(WithSession(session => DismRemoveCapability(session, name, 0, 0, 0)), "remove it");

    public static AppOutcome DisableFeature(string name) =>
        Outcome(WithSession(session => DismDisableFeature(session, name, null, 0, 0, 0, 0)), "turn it off");

    private static void Capabilities(uint session, List<AppItem> items)
    {
        nint list;
        uint count;
        if (DismGetCapabilities(session, &list, &count) < 0)
            return;

        try
        {
            for (var i = 0; i < count; i++)
            {
                var entry = list + i * ItemSize;
                var state = Marshal.ReadInt32(entry, StateOffset);
                if (state is not (StateInstalled or StateInstallPending or StateUninstallPending) || Text(entry, NameOffset) is not { } name)
                    continue;

                string? display = null, description = null;
                long? size = null;
                nint info;
                if (DismGetCapabilityInfo(session, name, &info) >= 0)
                {
                    display = Text(info, DisplayNameOffset);
                    description = Text(info, DescriptionOffset);
                    var installSize = (uint)Marshal.ReadInt32(info, InstallSizeOffset);
                    size = installSize > 0 ? installSize : null;
                    DismDelete(info);
                }

                // "Language.Basic~~~en-US~0.0.1.0": the name, then architecture, language and version.
                var parts = name.Split('~');
                items.Add(Item($"capability|{name}", display ?? parts[0], name, parts.Length > 1 ? Blank(parts[^1]) : null,
                    description, size, state, isCapability: true));
            }
        }
        finally
        {
            DismDelete(list);
        }
    }

    private static void Features(uint session, List<AppItem> items)
    {
        nint list;
        uint count;
        if (DismGetFeatures(session, null, PackageNone, &list, &count) < 0)
            return;

        try
        {
            for (var i = 0; i < count; i++)
            {
                var entry = list + i * ItemSize;
                var state = Marshal.ReadInt32(entry, StateOffset);
                if (state is not (StateInstalled or StateInstallPending or StateUninstallPending) || Text(entry, NameOffset) is not { } name)
                    continue;

                string? display = null, description = null;
                var restart = 0;
                nint info;
                if (DismGetFeatureInfo(session, name, null, PackageNone, &info) >= 0)
                {
                    display = Text(info, DisplayNameOffset);
                    description = Text(info, DescriptionOffset);
                    restart = Marshal.ReadInt32(info, RestartOffset);
                    DismDelete(info);
                }

                var item = Item($"feature|{name}", display ?? name, name, null, description, null, state, isCapability: false);
                if (restart == 2)
                    item = item with { Facts = [.. item.Facts, new AppFact("Restart", "Turning it off needs a restart")] };
                items.Add(item);
            }
        }
        finally
        {
            DismDelete(list);
        }
    }

    private static AppItem Item(string id, string name, string identity, string? version, string? description, long? size, int state, bool isCapability)
    {
        var facts = new List<AppFact> { new(isCapability ? "Capability" : "Feature name", identity) };
        if (description is not null)
            facts.Add(new AppFact("Description", description));

        var pending = state switch
        {
            StateInstallPending => ("Restart to finish", "Windows has added it, and finishes at the next restart."),
            StateUninstallPending => ("Restart to finish", "Windows has removed it, and finishes at the next restart."),
            _ => ((string?)null, (string?)null),
        };

        return new AppItem
        {
            Id = id,
            Group = AppGroup.Features,
            Name = name,
            InstalledWith = isCapability ? "Optional feature" : "Windows feature",
            Publisher = "Microsoft",
            Version = version,
            Size = size,
            About = isCapability
                ? "An optional feature of Windows, as Settings lists them: added on demand or with Windows. Removing it frees its space; it can be added again from Settings."
                : "A Windows feature, as \"Turn Windows features on or off\" lists them. Turning it off keeps its files, so it can be turned on again.",
            Facts = facts,
            UninstallText = isCapability ? "Remove" : "Turn off",
            Uninstall = state == StateUninstallPending ? null : isCapability ? new CapabilityRemoval(identity) : new FeatureRemoval(identity),
            Locked = state == StateUninstallPending ? "It's already being removed: restart to finish." : null,
            Problem = pending.Item1,
            ProblemExplanation = pending.Item2,
        };
    }

    /// <summary>Opens DISM on the running Windows, runs <paramref name="action"/>, and shuts DISM down again. Returns its HRESULT.</summary>
    private static int WithSession(Func<uint, int> action) => WinRt.OnThread(() =>
    {
        lock (Gate)
        {
            var hr = DismInitialize(LogErrors, null, null);
            if (hr < 0)
                return hr;

            try
            {
                uint session;
                hr = DismOpenSession(OnlineImage, null, null, &session);
                if (hr < 0)
                    return hr;

                try
                {
                    return action(session);
                }
                finally
                {
                    DismCloseSession(session);
                }
            }
            finally
            {
                DismShutdown();
            }
        }
    });

    private static AppOutcome Outcome(int hr, string what) => hr switch
    {
        ErrorSuccessRestartRequired => new AppOutcome(null, NeedsRestart: true),
        >= 0 => AppOutcome.Done,
        unchecked((int)0x800702E4) or unchecked((int)0x80070005) => new AppOutcome("Windows refused: it needs administrator rights."),
        unchecked((int)0x800F0806) => new AppOutcome("A change is already waiting for a restart. Restart, then try again."),
        unchecked((int)0x80070BC2) => new AppOutcome(null, NeedsRestart: true),
        _ => new AppOutcome($"Windows couldn't {what} (0x{hr:X8}). DISM's log, in the Windows folder under Logs\\DISM, says more."),
    };

    private static string? Text(nint structure, int offset) =>
        Marshal.ReadIntPtr(structure, offset) is var text and not 0 ? Blank(Marshal.PtrToStringUni(text)) : null;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DismInitialize(int logLevel, string? logFilePath, string? scratchDirectory);

    [LibraryImport("dismapi.dll")]
    private static partial int DismShutdown();

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DismOpenSession(string imagePath, string? windowsDirectory, string? systemDrive, uint* session);

    [LibraryImport("dismapi.dll")]
    private static partial int DismCloseSession(uint session);

    [LibraryImport("dismapi.dll")]
    private static partial int DismDelete(nint structure);

    [LibraryImport("dismapi.dll")]
    private static partial int DismGetCapabilities(uint session, nint* capabilities, uint* count);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DismGetCapabilityInfo(uint session, string name, nint* info);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DismGetFeatures(uint session, string? identifier, int packageIdentifier, nint* features, uint* count);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DismGetFeatureInfo(uint session, string featureName, string? identifier, int packageIdentifier, nint* info);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DismRemoveCapability(uint session, string name, nint cancelEvent, nint progress, nint userData);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DismDisableFeature(uint session, string featureName, string? packageName, int removePayload,
        nint cancelEvent, nint progress, nint userData);
}
