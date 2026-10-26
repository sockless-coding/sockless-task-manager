namespace Sockless.TaskManager.Features.Updates;

/// <summary>
/// Releases on GitHub. The latest tag is read from where /releases/latest redirects, which needs no
/// JSON and doesn't count against the API's rate limit. Drafts and prereleases are never "latest".
/// </summary>
internal static class ReleaseFeed
{
    public const string Repository = "https://github.com/sockless-coding/sockless-task-manager";

    private const string TagPath = "/releases/tag/";

    private static readonly HttpClient Probe = CreateClient(followRedirects: false);
    private static readonly HttpClient Downloads = CreateClient(followRedirects: true);

    /// <summary>The name the release workflow gives the zip; a .sha256 file sits next to it.</summary>
    public static string AssetName(string tag) => $"SocklessTaskManager-{tag}-win-x64.zip";

    public static string ReleasePage(string tag) => $"{Repository}{TagPath}{Uri.EscapeDataString(tag)}";

    /// <summary>The latest release's tag, or null while there is none.</summary>
    public static async Task<string?> LatestTagAsync(CancellationToken cancel)
    {
        using var response = await Probe.GetAsync($"{Repository}/releases/latest", HttpCompletionOption.ResponseHeadersRead, cancel);

        // With no release yet it redirects to /releases rather than /releases/tag/<tag>.
        var path = response.Headers.Location?.OriginalString;
        var at = path?.LastIndexOf(TagPath, StringComparison.Ordinal) ?? -1;
        return at < 0 ? null : Uri.UnescapeDataString(path![(at + TagPath.Length)..]);
    }

    public static async Task DownloadAsync(string tag, string fileName, string destination, CancellationToken cancel)
    {
        using var response = await Downloads.GetAsync(
            $"{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{fileName}", HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();

        await using var file = File.Create(destination);
        await response.Content.CopyToAsync(file, cancel);
    }

    private static HttpClient CreateClient(bool followRedirects)
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = followRedirects })
        {
            Timeout = TimeSpan.FromMinutes(followRedirects ? 5 : 1),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SocklessTaskManager/{ReleaseVersion.Current}");
        return client;
    }
}
