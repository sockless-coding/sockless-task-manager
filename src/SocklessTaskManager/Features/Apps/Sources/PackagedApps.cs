using Microsoft.Win32;

namespace Sockless.TaskManager.Features.Apps;

/// <summary>
/// MSIX packages installed for this user, as Settings lists them: Store apps, Windows' own apps,
/// and packages installed from a file or registered by a developer. Frameworks and Windows' own
/// system packages go in Components, where they can't be uninstalled. Read through the package
/// manager (Windows.Management.Deployment) by vtable.
/// </summary>
internal static unsafe class PackagedApps
{
    private const string ClassName = "Windows.Management.Deployment.PackageManager";
    private const string RepositoryPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    // PackageTypes: Main 0x1, Framework 0x2, Optional 0x20. Resource packages (languages, scales) and bundles are left out.
    private const uint ListedTypes = 0x1 | 0x2 | 0x20;

    private static readonly Guid IidPackageManager = new("9a7d4b65-5e8f-4fc7-a2e5-7f6925cb8b53");
    private static readonly Guid IidPackageManager2 = new("f7aad08d-0840-46f2-b5d8-cad47693a095");
    private static readonly Guid IidDeploymentResult = new("2563b9ae-b77d-4c1f-8a7b-20e6ad515ef3");
    private static readonly Guid IidPackage2 = new("a6612fb6-7688-4ace-95fb-359538e7aa01");
    private static readonly Guid IidPackage3 = new("5f738b61-f86a-4917-93d1-f1ee9d3b35d9");
    private static readonly Guid IidPackage4 = new("65aed1ae-b95b-450c-882b-6255187f397e");
    private static readonly Guid IidPackage8 = new("2c584f7b-ce2a-4be6-a093-77cfbb2a7ea1");

    /// <summary>The key Windows writes when this user's packages change, for the watcher.</summary>
    public static (RegistryHive Hive, string Path) Key => (RegistryHive.CurrentUser, RepositoryPath);

    public static List<AppItem> Scan() => WinRt.OnThread(() =>
    {
        var items = new List<AppItem>();
        var manager = Manager();
        if (manager == 0)
            return items;

        try
        {
            // IPackageManager2: FindPackagesByUserSecurityIdWithPackageTypes = 10 ("" is this user).
            var manager2 = WinRt.Query(manager, IidPackageManager2);
            if (manager2 == 0)
                return items;

            nint iterable;
            var user = WinRt.Create(string.Empty);
            int hr;
            try
            {
                hr = ((delegate* unmanaged<nint, nint, uint, nint*, int>)(*(nint**)manager2)[10])(manager2, user, ListedTypes, &iterable);
            }
            finally
            {
                WinRt.Take(user);
                WinRt.Release(manager2);
            }

            if (hr < 0)
                return items;

            try
            {
                foreach (var package in Iterate(iterable))
                {
                    try
                    {
                        if (Read(package) is { } item)
                            items.Add(item);
                    }
                    finally
                    {
                        WinRt.Release(package);
                    }
                }
            }
            finally
            {
                WinRt.Release(iterable);
            }
        }
        finally
        {
            WinRt.Release(manager);
        }

        return items;
    });

    /// <summary>Removes the package for this user; waits for Windows to finish.</summary>
    public static AppOutcome Remove(string fullName) => WinRt.OnThread(() =>
    {
        var manager = Manager();
        if (manager == 0)
            return new AppOutcome("Windows' package manager isn't available.");

        try
        {
            // IPackageManager: RemovePackageAsync = 8; the operation's GetResults = 10 gives an IDeploymentResult.
            nint operation;
            var name = WinRt.Create(fullName);
            int hr;
            try
            {
                hr = ((delegate* unmanaged<nint, nint, nint*, int>)(*(nint**)manager)[8])(manager, name, &operation);
            }
            finally
            {
                WinRt.Take(name);
            }

            if (hr < 0)
                return new AppOutcome(Describe(hr, null));

            try
            {
                var error = WinRt.Wait(operation, TimeSpan.FromMinutes(15));
                if (error >= 0)
                    return AppOutcome.Done;

                string? text = null;
                var result = WinRt.GetObject(operation, 10);
                if (result != 0)
                {
                    var deployment = WinRt.Query(result, IidDeploymentResult);
                    text = WinRt.GetString(deployment, 6);
                    WinRt.Release(deployment);
                    WinRt.Release(result);
                }

                return new AppOutcome(Describe(error, text));
            }
            finally
            {
                WinRt.Release(operation);
            }
        }
        finally
        {
            WinRt.Release(manager);
        }
    });

    private static nint Manager()
    {
        var instance = WinRt.Activate(ClassName);
        if (instance == 0)
            return 0;

        var manager = WinRt.Query(instance, IidPackageManager);
        WinRt.Release(instance);
        return manager;
    }

    /// <summary>IIterable&lt;Package&gt;: First = 6; IIterator: get_Current 6, get_HasCurrent 7, MoveNext 8. The caller releases each.</summary>
    private static List<nint> Iterate(nint iterable)
    {
        var packages = new List<nint>(400);
        var iterator = WinRt.GetObject(iterable, 6);
        if (iterator == 0)
            return packages;

        try
        {
            var hasCurrent = WinRt.GetBool(iterator, 7) ?? false;
            while (hasCurrent)
            {
                if (WinRt.GetObject(iterator, 6) is var package and not 0)
                    packages.Add(package);

                byte more;
                if (((delegate* unmanaged<nint, byte*, int>)(*(nint**)iterator)[8])(iterator, &more) < 0)
                    break;
                hasCurrent = more != 0;
            }
        }
        finally
        {
            WinRt.Release(iterator);
        }

        return packages;
    }

    private static AppItem? Read(nint package)
    {
        // IPackage: get_Id 6, get_IsFramework 8. IPackageId: Name 6, Version 7, Architecture 8, Publisher 10, FullName 12, FamilyName 13.
        var id = WinRt.GetObject(package, 6);
        if (id == 0)
            return null;

        string? fullName, packageName, familyName, publisherSubject;
        string? version = null;
        int? architecture;
        try
        {
            fullName = WinRt.GetString(id, 12);
            packageName = WinRt.GetString(id, 6);
            familyName = WinRt.GetString(id, 13);
            publisherSubject = WinRt.GetString(id, 10);
            architecture = WinRt.GetInt(id, 8);
            ulong packed;
            if (((delegate* unmanaged<nint, ulong*, int>)(*(nint**)id)[7])(id, &packed) >= 0)
                version = $"{(ushort)packed}.{(ushort)(packed >> 16)}.{(ushort)(packed >> 32)}.{(ushort)(packed >> 48)}";
        }
        finally
        {
            WinRt.Release(id);
        }

        if (fullName is not { Length: > 0 } || packageName is null)
            return null;

        var isFramework = WinRt.GetBool(package, 8) ?? false;

        // IPackage2: DisplayName 6, PublisherDisplayName 7, Description 8, IsDevelopmentMode 12.
        string? displayName = null, publisher = null, description = null;
        var isDevelopment = false;
        var p2 = WinRt.Query(package, IidPackage2);
        if (p2 != 0)
        {
            displayName = WinRt.GetString(p2, 6);
            publisher = WinRt.GetString(p2, 7);
            description = WinRt.GetString(p2, 8);
            isDevelopment = WinRt.GetBool(p2, 12) ?? false;
            WinRt.Release(p2);
        }

        // IPackage3: get_Status 6 (IPackageStatus), get_InstalledDate 7 (a DateTime: 100 ns ticks since 1601, UTC).
        DateTime? installed = null;
        string? problem = null, explanation = null;
        var p3 = WinRt.Query(package, IidPackage3);
        if (p3 != 0)
        {
            long ticks;
            if (((delegate* unmanaged<nint, long*, int>)(*(nint**)p3)[7])(p3, &ticks) >= 0 && ticks > 0)
                installed = DateTime.FromFileTimeUtc(ticks).ToLocalTime();

            var status = WinRt.GetObject(p3, 6);
            if (status != 0)
            {
                (problem, explanation) = StatusProblem(status);
                WinRt.Release(status);
            }

            WinRt.Release(p3);
        }

        // IPackage4: get_SignatureKind 6 (0 none, 1 developer, 2 enterprise, 3 Store, 4 system), get_IsOptional 7.
        var signature = 3;
        var isOptional = false;
        var p4 = WinRt.Query(package, IidPackage4);
        if (p4 != 0)
        {
            signature = WinRt.GetInt(p4, 6) ?? 3;
            isOptional = WinRt.GetBool(p4, 7) ?? false;
            WinRt.Release(p4);
        }

        // IPackage8: get_InstalledPath 9, get_IsStub 17 (Windows 10 2004 and later).
        string? location = null;
        var isStub = false;
        var p8 = WinRt.Query(package, IidPackage8);
        if (p8 != 0)
        {
            location = WinRt.GetString(p8, 9);
            isStub = WinRt.GetBool(p8, 17) ?? false;
            WinRt.Release(p8);
        }

        if (location is { Length: > 0 } && !Directory.Exists(location))
            location = null;

        var name = displayName is { Length: > 0 } && !displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? displayName : packageName;
        var isSystem = signature == 4;
        var group = isSystem || isFramework ? AppGroup.Components : AppGroup.Packaged;
        var installedWith = isFramework ? "Framework package"
            : isSystem ? "Part of Windows"
            : isDevelopment ? "Developer mode"
            : signature switch
            {
                3 => isOptional ? "Store add-on" : "Microsoft Store",
                2 => "Package file (enterprise)",
                1 => "Package file",
                _ => "Package",
            };

        var facts = new List<AppFact>
        {
            new("Package", fullName),
            new("Family name", familyName ?? string.Empty),
        };
        if (publisherSubject is { Length: > 0 })
            facts.Add(new AppFact("Signed by", publisherSubject));
        if (description is { Length: > 0 } && !description.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            facts.Add(new AppFact("Description", description));
        if (isStub)
            facts.Add(new AppFact("Stub", "A placeholder: the full app downloads when it's first opened"));

        return new AppItem
        {
            Id = $"package|{fullName}",
            Group = group,
            Name = name,
            InstalledWith = installedWith,
            Publisher = publisher is { Length: > 0 } && !publisher.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? publisher : null,
            Version = version,
            InstalledOn = installed,
            IsPerUser = true,
            Architecture = architecture switch
            {
                0 => "32-bit",
                9 => "64-bit",
                12 => "ARM64",
                5 => "ARM",
                11 => "Neutral",
                _ => null,
            },
            Location = location,
            About = isFramework
                ? "A framework package other packaged apps are built on. Windows installs it with them and keeps it while any of them needs it."
                : isSystem
                    ? "Part of Windows, installed and updated with it."
                    : isDevelopment
                        ? "Registered from a folder by a developer (developer mode), for you only."
                        : signature == 3
                            ? "An MSIX package from the Microsoft Store (or one of Windows' own apps, which update through it), installed for you. Uninstalling removes it and its data for your account."
                            : "An MSIX package installed for you from a package file. Uninstalling removes it and its data for your account.",
            Facts = facts,
            RegistryKey = $@"HKCU\{RepositoryPath}\{fullName}",
            Uninstall = isFramework || isSystem ? null : new PackageRemoval(fullName),
            Locked = isFramework
                ? "Other apps are built on it: Windows keeps it while any of them needs it. Uninstall those apps instead."
                : isSystem
                    ? "Part of Windows: it can't be uninstalled."
                    : null,
            Problem = problem,
            ProblemExplanation = explanation,
        };
    }

    /// <summary>IPackageStatus: VerifyIsOK 6, then NotAvailable 7 … DeploymentInProgress 17.</summary>
    private static (string?, string?) StatusProblem(nint status)
    {
        if (WinRt.GetBool(status, 6) ?? true)
            return (null, null);

        if (WinRt.GetBool(status, 11) == true || WinRt.GetBool(status, 14) == true || WinRt.GetBool(status, 13) == true)
            return ("Needs repair", "Windows found its files changed or damaged. Repair or reset it in Settings, or reinstall it from the Store.");
        if (WinRt.GetBool(status, 12) == true)
            return ("License problem", "Its license isn't valid on this computer. Open it from the Store to renew it, or reinstall it.");
        if (WinRt.GetBool(status, 15) == true)
            return ("Missing dependency", "A package it needs isn't installed or doesn't work. Reinstalling it usually brings it back.");
        if (WinRt.GetBool(status, 7) == true || WinRt.GetBool(status, 8) == true)
            return ("Not available", "Its files aren't available: they may be on a drive that isn't connected.");
        if (WinRt.GetBool(status, 16) == true || WinRt.GetBool(status, 17) == true)
            return ("Being updated", "Windows is installing, updating or removing it right now.");
        if (WinRt.GetBool(status, 10) == true)
            return ("Disabled", "An administrator or policy has disabled it.");
        return ("Needs attention", "Windows says it isn't in a usable state.");
    }

    private static string Describe(int hr, string? text) => hr switch
    {
        unchecked((int)0x80070005) => "Windows refused: it needs administrator rights.",
        unchecked((int)0x80073CFA) => text is { Length: > 0 } ? $"Windows couldn't remove it: {text}" : "Windows couldn't remove it: it may be part of Windows, or required by policy.",
        unchecked((int)0x80073D02) => "It's in use. Close it and try again.",
        unchecked((int)0x800705B4) => "Windows is taking too long: it may still be removing it. Refresh in a while to see.",
        _ => text is { Length: > 0 } ? $"Windows couldn't remove it: {text}" : $"Windows couldn't remove it (0x{hr:X8}).",
    };
}
