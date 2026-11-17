namespace Sockless.TaskManager.Core;

/// <summary>
/// What another page wants the Processes page to show. Other pages list processes in their own
/// terms (memory shares, a user's processes) but open them here, where the details, actions and
/// drill-downs of a process live.
/// </summary>
/// <param name="User">Only processes this account runs (the name without its domain, as the User column shows it).</param>
/// <param name="SessionId">Only processes in this session.</param>
/// <param name="ProcessId">Open this process's details; the list's filters are left as they are.</param>
/// <param name="CreateTime">With <paramref name="ProcessId"/>: FILETIME (UTC), so a reused id doesn't open another process.</param>
/// <param name="Folder">Only processes whose program is in this folder or under it (an app's install folder).</param>
public sealed record ProcessQuery(string? User = null, int? SessionId = null, int? ProcessId = null, long CreateTime = 0, string? Folder = null);

/// <summary>
/// Moves between pages without slices knowing each other. A page asks for processes with
/// <see cref="ShowProcesses"/>; the Processes feature applies the query and brings itself forward
/// with <see cref="Open"/>, which the shell follows. UI thread only.
/// </summary>
public sealed class Navigator
{
    /// <summary>The page on screen, kept by the shell: where a request came from, to go back to.</summary>
    public Feature? Current { get; set; }

    /// <summary>Raised for <see cref="ShowProcesses"/>; the Processes feature handles it.</summary>
    public event Action<ProcessQuery>? ProcessesRequested;

    /// <summary>A feature asked to be shown; the shell selects it.</summary>
    public event Action<Feature>? OpenRequested;

    public void ShowProcesses(ProcessQuery query) => ProcessesRequested?.Invoke(query);

    public void Open(Feature feature) => OpenRequested?.Invoke(feature);
}
