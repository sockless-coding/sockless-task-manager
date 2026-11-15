namespace Sockless.TaskManager.Core;

/// <summary>
/// The program in a command line as Windows registers it: in a service's ImagePath, a Run key, a
/// scheduled task. Shared by Services (to group by publisher) and Autostart (to verify what runs).
/// </summary>
internal static class CommandLine
{
    private static readonly string WindowsFolder = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private static readonly string System32 = Environment.SystemDirectory;

    private static readonly string[] Extensions = [".exe", ".dll", ".sys", ".com", ".bat", ".cmd", ".scr", ".cpl", ".ocx", ".ps1", ".vbs", ".js", ".lnk", ".msc"];

    /// <summary>
    /// The program in a registered command line: quoted, or the longest prefix ending in .exe/.sys/.dll
    /// (paths with spaces are often registered unquoted), with the kernel's path forms made ordinary.
    /// A bare name is put in System32, as the service control manager does.
    /// </summary>
    public static string ProgramPath(string commandLine)
    {
        var (path, _) = Split(commandLine);
        return path.Length == 0 ? string.Empty : Normalize(path, searchPath: false);
    }

    /// <summary>
    /// The program and its arguments. Bare names are looked for the way a shell would start them:
    /// System32, the Windows folder, then PATH, with .exe added when there's no extension.
    /// </summary>
    public static (string Program, string Arguments) Resolve(string commandLine)
    {
        var (path, arguments) = Split(commandLine);
        return path.Length == 0 ? (string.Empty, string.Empty) : (Normalize(path, searchPath: true), arguments);
    }

    /// <summary>
    /// A path as a registration names it (environment variables, \SystemRoot\, \??\, bare names),
    /// made ordinary. Unlike <see cref="Resolve"/> it doesn't split off arguments.
    /// </summary>
    public static string Normalize(string path, bool searchPath = true)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path.Length == 0)
            return path;

        if (path.StartsWith(@"\??\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            return Path.Join(WindowsFolder, path[12..]);
        if (path.StartsWith(@"SystemRoot\", StringComparison.OrdinalIgnoreCase))
            return Path.Join(WindowsFolder, path[11..]);
        if (path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
            return Path.Join(WindowsFolder, path);
        if (Path.IsPathRooted(path))
            return path;

        if (!searchPath)
            return Path.Join(System32, path);

        var names = Path.HasExtension(path) ? [path] : new[] { path + ".exe", path + ".dll", path };
        foreach (var name in names)
        {
            foreach (var folder in SearchFolders())
            {
                var candidate = Path.Join(folder, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return Path.Join(System32, names[0]);
    }

    /// <summary>Quoted, or the longest prefix ending in a program's extension, or up to the first space.</summary>
    private static (string Path, string Arguments) Split(string commandLine)
    {
        var text = commandLine.Trim();
        if (text.Length == 0)
            return (string.Empty, string.Empty);

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 0 ? (text[1..end], text[(end + 1)..].Trim()) : (text[1..], string.Empty);
        }

        var best = -1;
        foreach (var extension in Extensions)
        {
            var at = 0;
            while ((at = text.IndexOf(extension, at, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                var end = at + extension.Length;
                if (end == text.Length || text[end] is ' ' or ',' or '\t')
                {
                    if (best < 0 || end < best)
                        best = end;
                    break;
                }

                at = end;
            }
        }

        if (best > 0)
            return (text[..best], text[best..].TrimStart(',', ' ', '\t'));

        var space = text.IndexOf(' ');
        return space > 0 ? (text[..space], text[(space + 1)..].Trim()) : (text, string.Empty);
    }

    private static IEnumerable<string> SearchFolders()
    {
        yield return System32;
        yield return WindowsFolder;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string expanded;
            try
            {
                expanded = Environment.ExpandEnvironmentVariables(folder);
                if (!Path.IsPathFullyQualified(expanded))
                    continue;
            }
            catch (ArgumentException)
            {
                continue;
            }

            yield return expanded;
        }
    }
}
