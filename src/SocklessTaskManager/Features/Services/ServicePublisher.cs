using System.Diagnostics;
using Microsoft.Win32;

namespace Sockless.TaskManager.Features.Services;

/// <summary>What group a service goes in, and why.</summary>
/// <param name="Name">"Windows", "Microsoft", "Bitdefender"…</param>
/// <param name="Program">The file that runs: the service DLL for services that share svchost.exe.</param>
/// <param name="Product">The file's product name, when it says.</param>
/// <param name="IsMissing">The registered program isn't on disk: often a leftover of something uninstalled.</param>
internal sealed record Publisher(string Name, string Program, string? Product, bool IsMissing)
{
    public const string WindowsName = "Windows";
    public const string MicrosoftName = "Microsoft";
    public const string UnknownName = "Unknown publisher";
    public const string MissingName = "Program not found";
}

/// <summary>
/// Groups services by who made them, from the company name in the program's version resource.
/// Services hosted by svchost.exe are judged by their ServiceDll. Microsoft's files under the
/// Windows folder are "Windows"; elsewhere (Edge, Office, Defender's platform) they're "Microsoft".
/// Version resources are read once per file and kept: sampling thread only.
/// </summary>
internal sealed class ServicePublisher
{
    private static readonly string WindowsFolder = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private static readonly string System32 = Environment.SystemDirectory;

    private static readonly string[] ProgramFolders =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    ];

    /// <summary>Trailing words that only say what kind of company it is, so "Foo, Inc." and "Foo Inc" group together.</summary>
    private static readonly string[] LegalSuffixes =
    [
        "Incorporated", "Inc", "Corporation", "Corp", "Co", "Company", "Ltd", "Limited", "LLC", "L.L.C", "GmbH", "AG", "SA", "S.A",
        "SRL", "S.R.L", "BV", "B.V", "AB", "AS", "A/S", "Oy", "SE", "KG", "PLC", "Pty", "S.p.A", "SpA", "NV", "N.V", "K.K",
    ];

    /// <summary>Companies whose files name them more than one way.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Advanced Micro Devices"] = "AMD",
        ["Hewlett-Packard"] = "HP",
        ["Hewlett Packard"] = "HP",
        ["Intel(R)"] = "Intel",
    };

    private readonly Dictionary<string, (string? Company, string? Product, bool Exists)> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One display name per company key, the first seen.</summary>
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    public Publisher Of(string serviceName, uint type, string imagePath)
    {
        var program = ProgramPath(imagePath);
        if (program.Length == 0)
            return new Publisher(Publisher.UnknownName, string.Empty, null, false);

        if (IsSvchost(program) && ServiceDll(serviceName) is { } dll)
            program = dll;

        var (company, product, exists) = Read(program);
        if (!exists)
            return new Publisher(Publisher.MissingName, program, null, true);

        var inWindows = IsUnder(program, WindowsFolder);
        if (company is not null && company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            return new Publisher(inWindows || (type & (ServiceManager.KernelDriver | ServiceManager.FileSystemDriver)) != 0
                ? Publisher.WindowsName
                : Publisher.MicrosoftName, program, product, false);

        if (company is not null && Normalize(company) is { Length: > 0 } normalized)
        {
            var name = Aliases.GetValueOrDefault(normalized, normalized);
            // "Brave Software" and "BraveSoftware" are one publisher: the first spelling seen names it.
            var key = name.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (!_names.TryGetValue(key, out var shown))
                _names[key] = shown = name;
            return new Publisher(shown, program, product, false);
        }

        // No company in the file: the folder it's installed in says more than nothing.
        if (FolderUnderPrograms(program) is { } folder)
            return new Publisher(folder, program, product, false);

        return new Publisher(inWindows ? Publisher.WindowsName : Publisher.UnknownName, program, product, false);
    }

    /// <summary>
    /// The program in a registered command line: quoted, or the longest prefix ending in .exe/.sys/.dll
    /// (paths with spaces are often registered unquoted), with the kernel's path forms made ordinary.
    /// </summary>
    public static string ProgramPath(string imagePath)
    {
        var text = imagePath.Trim();
        if (text.Length == 0)
            return string.Empty;

        string path;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            path = end > 0 ? text[1..end] : text[1..];
        }
        else
        {
            path = text;
            var found = false;
            foreach (var extension in (ReadOnlySpan<string>)[".exe", ".sys", ".dll"])
            {
                var at = text.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
                if (at > 0 && (at + 4 == text.Length || text[at + 4] == ' '))
                {
                    path = text[..(at + 4)];
                    found = true;
                    break;
                }
            }

            if (!found && text.IndexOf(' ') is var space and > 0)
                path = text[..space];
        }

        path = Environment.ExpandEnvironmentVariables(path);
        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
            path = path[4..];
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            path = Path.Join(WindowsFolder, path[12..]);
        else if (path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
            path = Path.Join(WindowsFolder, path);
        else if (!Path.IsPathRooted(path))
            path = Path.Join(System32, path);

        return path;
    }

    private static bool IsSvchost(string program) =>
        string.Equals(Path.GetFileName(program), "svchost.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>A shared-process service's own code: Services\name\Parameters\ServiceDll, or on the service key itself.</summary>
    private static string? ServiceDll(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key is null)
                return null;

            using var parameters = key.OpenSubKey("Parameters");
            var value = parameters?.GetValue("ServiceDll") as string ?? key.GetValue("ServiceDll") as string;
            return string.IsNullOrWhiteSpace(value) ? null : Environment.ExpandEnvironmentVariables(value.Trim());
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private (string? Company, string? Product, bool Exists) Read(string program)
    {
        if (_files.TryGetValue(program, out var known))
            return known;

        (string?, string?, bool) info = (null, null, false);
        try
        {
            if (File.Exists(program))
            {
                var version = FileVersionInfo.GetVersionInfo(program);
                info = (Blank(version.CompanyName), Blank(version.ProductName), true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Unreadable, but there: group it by its folder.
            info = (null, null, true);
        }

        _files[program] = info;
        return info;
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>"Bitdefender S.R.L." → "Bitdefender", "NVIDIA Corporation" → "NVIDIA", "Google LLC" → "Google".</summary>
    internal static string Normalize(string company)
    {
        var name = company.Replace("®", string.Empty, StringComparison.Ordinal)
            .Replace("™", string.Empty, StringComparison.Ordinal)
            .Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        // "Copyright (c) Foo" sometimes lands in CompanyName.
        if (name.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase))
        {
            name = name[9..].TrimStart();
            if (name.StartsWith("(c)", StringComparison.OrdinalIgnoreCase))
                name = name[3..];
            name = name.TrimStart(' ', '©');
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            name = name.TrimEnd(' ', ',', '.', '-');
            foreach (var suffix in LegalSuffixes)
            {
                if (name.Length > suffix.Length + 1
                    && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    && name[^(suffix.Length + 1)] is ' ' or ',')
                {
                    name = name[..^suffix.Length];
                    changed = true;
                    break;
                }
            }
        }

        return name.Trim();
    }

    private static bool IsUnder(string path, string folder) =>
        folder.Length > 0
        && path.Length > folder.Length
        && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
        && path[folder.Length] == Path.DirectorySeparatorChar;

    /// <summary>C:\Program Files\Foo\bar.exe → "Foo".</summary>
    private static string? FolderUnderPrograms(string program)
    {
        foreach (var root in ProgramFolders)
        {
            if (!IsUnder(program, root))
                continue;

            var rest = program[(root.Length + 1)..];
            var slash = rest.IndexOf(Path.DirectorySeparatorChar);
            return slash > 0 ? rest[..slash] : null;
        }

        return null;
    }
}
