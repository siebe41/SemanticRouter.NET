namespace SemanticRouter.Desktop;

/// <summary>
/// The app window's web view profile (<c>webview/</c> in the configuration directory). The router marks its API answers
/// <c>no-store</c>, so the web view keeps no prompt previews in its HTTP cache; the cache is still emptied before each
/// window opens, for whatever an older version let it keep, so deleting activity on the dashboard is not undone by a
/// copy in the profile.
/// </summary>
internal static class WebViewProfile
{
    /// <summary>The HTTP cache directories of each web view, relative to the profile.</summary>
    internal static readonly string[] CacheDirectories =
    [
        "cache", // WebKitGTK and WPE (Linux)
        Path.Combine("WebView2", "EBWebView", "Default", "Cache"), // WebView2 (Windows)
    ];

    /// <summary>Deletes the HTTP caches in <paramref name="directory"/>. Best effort: what cannot be deleted is logged and left.</summary>
    public static void ClearHttpCache(string directory, TextWriter log)
    {
        foreach (string cache in CacheDirectories.Select(c => Path.Combine(directory, c)).Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(cache, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.WriteLine($"The app window's web view cache {cache} could not be emptied: {ex.Message}");
            }
        }
    }
}
