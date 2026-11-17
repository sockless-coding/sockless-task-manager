using System.Diagnostics;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>
/// Reads devices, driver services and loaded kernel modules, then the driver files: version
/// resources at once, signatures after (catalog lookups for Windows' own take a few seconds for a
/// few hundred files). Signatures are kept between scans while a file's size and time stay the same,
/// so a rescan after a device change is quick. One scan at a time, on a background thread.
/// </summary>
internal sealed class DriverScanner
{
    private static readonly TimeSpan PublishEvery = TimeSpan.FromSeconds(1.5);

    private readonly Dictionary<string, (long Length, DateTime Modified, Signature Signature)> _signatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly DriverOrigins _origins = new();
    private byte[] _moduleBuffer = NtSystem.Buffer(64 * 1024);

    public void Scan(Action<DriverSnapshot> publish)
    {
        var devices = Safe(DeviceTree.Read, "devices") ?? [];
        var services = Safe(DriverServices.Read, "driver services") ?? new Dictionary<string, DriverService>(StringComparer.OrdinalIgnoreCase);
        var loaded = (Safe(() => KernelModules.Read(ref _moduleBuffer), "kernel modules") ?? []).Select(DumpCopy).ToList();

        // Each service's file, by path: how a module finds the service that loaded it.
        var byImage = new Dictionary<string, DriverService>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in services.Values)
        {
            if (service.Image is { } image)
                byImage.TryAdd(image, service);
        }

        var paths = loaded.Select(m => m.Path)
            .Concat(devices.Select(d => d.Service is { } s && services.TryGetValue(s, out var service) ? service.Image : null).OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, DriverFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
            files[path] = Read(path);

        OriginIndex? origins = null;
        var deviceOrigins = new Dictionary<string, DriverOrigin>(StringComparer.OrdinalIgnoreCase);

        DriverSnapshot Build(bool checking)
        {
            var modules = loaded.Select(m => new LoadedModule(m.Name, m.Path, m.ImageBytes, byImage.GetValueOrDefault(m.Path), files[m.Path])).ToList();
            var all = new Dictionary<string, DriverOrigin>(deviceOrigins, StringComparer.OrdinalIgnoreCase);
            if (origins is not null)
            {
                // A module's origin follows its file's signature (Windows' own or not), so it's worked out each time.
                foreach (var module in modules)
                {
                    var device = module.Service is { } service ? devices.FirstOrDefault(d => d.IsPresent && string.Equals(d.Service, service.Name, StringComparison.OrdinalIgnoreCase)) : null;
                    var deviceOrigin = device is null ? null : deviceOrigins.GetValueOrDefault(device.InstanceId);
                    if (origins.Resolve(module, device, deviceOrigin) is { } origin)
                        all.TryAdd(module.Path, origin);
                }
            }

            return new DriverSnapshot(devices, modules, services, new Dictionary<string, DriverFile>(files, StringComparer.OrdinalIgnoreCase),
                all, origins?.Tools ?? [], checking);
        }

        var pending = files.Values.Where(f => f.Signature.State == SignatureState.Pending).Select(f => f.Path).ToList();
        publish(Build(true));

        // Where drivers came from: the setup logs, and Windows Update's history for drivers older than them.
        try
        {
            origins = _origins.Read(needsHistory: devices.Any(d => d.IsThirdParty));
            foreach (var device in devices)
            {
                if (origins.Resolve(device) is { } origin)
                    deviceOrigins[device.InstanceId] = origin;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reading driver origins failed: {ex}");
        }

        publish(Build(pending.Count > 0));
        if (pending.Count == 0)
            return;

        using var checker = new SignatureChecker();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < pending.Count; i++)
        {
            var file = files[pending[i]];
            Signature signature;
            try
            {
                signature = checker.Check(file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                signature = new Signature(SignatureState.Unreadable);
            }

            files[file.Path] = file with { Signature = signature };
            if (Stamp(file.Path) is { } stamp)
                _signatures[file.Path] = (stamp.Length, stamp.Modified, signature);

            if (clock.Elapsed > PublishEvery && i < pending.Count - 1)
            {
                publish(Build(true));
                clock.Restart();
            }
        }

        publish(Build(false));
    }

    /// <summary>
    /// The crash dump stack loads copies of the storage drivers as "dump_" + their name
    /// (dump_stornvme.sys); the file on disk is the original.
    /// </summary>
    private static KernelModule DumpCopy(KernelModule module)
    {
        if (!module.Name.StartsWith("dump_", StringComparison.OrdinalIgnoreCase) || File.Exists(module.Path))
            return module;

        var original = Path.Combine(Path.GetDirectoryName(module.Path) ?? string.Empty, module.Name[5..]);
        return File.Exists(original) ? module with { Path = original } : module;
    }

    private DriverFile Read(string path)
    {
        if (Stamp(path) is not { } stamp)
            return DriverFile.Missing(path);

        string? company = null, description = null, version = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            company = Blank(info.CompanyName);
            description = Blank(info.FileDescription);
            // Windows' own say "10.0.26100.1 (WinBuild.160101.0800)": the build label adds nothing here.
            version = Blank(info.FileVersion) is { } v && v.IndexOf(" (", StringComparison.Ordinal) is > 0 and var cut ? v[..cut] : Blank(info.FileVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        var signature = _signatures.TryGetValue(path, out var known) && known.Length == stamp.Length && known.Modified == stamp.Modified
            ? known.Signature
            : Signature.Pending;
        return new DriverFile(path, true, company, description, version, stamp.Modified.ToLocalTime(), signature);
    }

    private static (long Length, DateTime Modified)? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static T? Safe<T>(Func<T> read, string what) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            // One unreadable source mustn't empty the page.
            Debug.WriteLine($"Reading {what} failed: {ex}");
            return null;
        }
    }
}
