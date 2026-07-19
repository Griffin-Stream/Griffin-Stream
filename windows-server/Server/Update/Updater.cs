using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace PCRemote.Server.Update;

/// <summary>Details of an available newer release.</summary>
public sealed record UpdateInfo(Version Latest, string Tag, string DownloadUrl, string ReleaseUrl);

/// <summary>
/// Checks GitHub Releases for a newer server build and, on request, downloads the installer and
/// starts a silent update helper. The helper waits for this process to exit, runs Setup with
/// /VERYSILENT, then relaunches Server.exe — no interactive wizard or force-close dialogs.
/// </summary>
public static class Updater
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/Griffin-Stream/Griffin-Stream/releases/latest";

    // Stable, unversioned installer asset name produced by build-release.ps1.
    private const string InstallerAssetName = "GriffinStreamServer-Setup.exe";

    /// <summary>Named mutex shared with Inno <c>AppMutex</c> so Setup can wait for a clean exit.</summary>
    public const string AppMutexName = "GriffinStreamServer";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>The most recent available update found by a check, or null if up to date/unknown.</summary>
    public static UpdateInfo? Available { get; private set; }

    /// <summary>Raised when a check finds a newer release than the running build.</summary>
    public static event Action<UpdateInfo>? UpdateFound;

    public static Version CurrentVersion => Normalize(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Human-readable version for the dashboard (e.g. "1.3.3").</summary>
    public static string DisplayVersion
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath
                    ?? Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var vi = FileVersionInfo.GetVersionInfo(path);
                    var raw = vi.ProductVersion ?? vi.FileVersion;
                    if (!string.IsNullOrWhiteSpace(raw))
                        return raw.Split('+')[0].Trim();
                }
            }
            catch { /* fall through */ }
            var v = CurrentVersion;
            return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
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
            return null;
        }
    }

    /// <summary>
    /// Download the installer, start a helper that waits for this process to exit, runs Setup
    /// silently, then relaunches the server. Caller should exit immediately on success.
    /// <paramref name="progress"/> reports 0..1 download fraction when provided.
    /// </summary>
    public static async Task<bool> DownloadAndRunAsync(
        UpdateInfo info,
        IProgress<double>? progress = null)
    {
        try
        {
            var dest = Path.Combine(Path.GetTempPath(), InstallerAssetName);
            using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1L;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long readTotal = 0;
                int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, n));
                    readTotal += n;
                    if (total > 0)
                        progress?.Report(Math.Clamp(readTotal / (double)total, 0, 1));
                }
                progress?.Report(1);
            }

            var appPath = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "Server.exe");
            var pid = Environment.ProcessId;
            var helperPath = Path.Combine(Path.GetTempPath(), $"GriffinStream-update-{pid}.ps1");

            // Escape single quotes for PowerShell single-quoted strings.
            static string PsLiteral(string s) => s.Replace("'", "''");

            var script =
                "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
                $"$targetPid = {pid}\r\n" +
                $"$setup = '{PsLiteral(dest)}'\r\n" +
                $"$app = '{PsLiteral(appPath)}'\r\n" +
                "$deadline = (Get-Date).AddMinutes(5)\r\n" +
                "while ((Get-Process -Id $targetPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {\r\n" +
                "  Start-Sleep -Seconds 1\r\n" +
                "}\r\n" +
                "Start-Process -FilePath $setup -ArgumentList " +
                "'/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CLOSEAPPLICATIONS','/FORCECLOSEAPPLICATIONS' -Wait\r\n" +
                "Start-Process -FilePath $app\r\n" +
                "Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue\r\n";

            await File.WriteAllTextAsync(helperPath, script);

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{helperPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
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
        var cleaned = tag.TrimStart('v', 'V').Trim();
        if (!Version.TryParse(cleaned, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
}
