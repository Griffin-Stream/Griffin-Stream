using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace PCRemote.Server.Update;

/// <summary>Details of an available newer release.</summary>
public sealed record UpdateInfo(Version Latest, string Tag, string DownloadUrl, string ReleaseUrl);

/// <summary>
/// Checks GitHub Releases for a newer server build and, on request, downloads the installer and
/// launches it. The "reliable source" is the project's own GitHub releases
/// (Griffin-Stream/Griffin-Stream) - the same place griffinstream.app/download resolves to.
///
/// All network work is best-effort and offline-safe: a failed check simply reports "no update"
/// and never interrupts the running server.
/// </summary>
public static class Updater
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/Griffin-Stream/Griffin-Stream/releases/latest";

    // Stable, unversioned installer asset name produced by build-release.ps1.
    private const string InstallerAssetName = "GriffinStreamServer-Setup.exe";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>The most recent available update found by a check, or null if up to date/unknown.</summary>
    public static UpdateInfo? Available { get; private set; }

    /// <summary>Raised when a check finds a newer release than the running build.</summary>
    public static event Action<UpdateInfo>? UpdateFound;

    public static Version CurrentVersion => Normalize(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub's API rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GriffinStreamServer");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>
    /// Query GitHub for the latest release. Returns the update if it is newer than the running
    /// build and ships the installer asset; otherwise null. Never throws.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            var body = await Http.GetStringAsync(LatestReleaseApi);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            if (!TryParseVersion(tag, out var latest)) return null;

            var htmlUrl = root.TryGetProperty("html_url", out var h) ? (h.GetString() ?? "") : "";

            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.Equals(name, InstallerAssetName, StringComparison.OrdinalIgnoreCase) &&
                        asset.TryGetProperty("browser_download_url", out var u))
                    {
                        downloadUrl = u.GetString();
                        break;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(downloadUrl)) return null;

            if (latest <= CurrentVersion)
            {
                Available = null;
                return null;
            }

            var info = new UpdateInfo(latest, tag, downloadUrl!, htmlUrl);
            Available = info;
            try { UpdateFound?.Invoke(info); } catch { /* subscribers best-effort */ }
            return info;
        }
        catch
        {
            // Offline / rate-limited / parse error: treat as "no update".
            return null;
        }
    }

    /// <summary>
    /// Download the installer for <paramref name="info"/> to a temp file and launch it. On success
    /// the caller should shut the server down so the installer can replace files in place.
    /// Returns true if the installer was started.
    /// </summary>
    public static async Task<bool> DownloadAndRunAsync(UpdateInfo info)
    {
        try
        {
            var dest = Path.Combine(Path.GetTempPath(), InstallerAssetName);
            using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(fs);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = dest,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        // Tags look like "v1.3.0" or "1.3.0".
        var cleaned = tag.TrimStart('v', 'V').Trim();
        if (!Version.TryParse(cleaned, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    /// <summary>Reduce to major.minor.build (ignore revision) so 1.3.0 == 1.3.0.0.</summary>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
}
