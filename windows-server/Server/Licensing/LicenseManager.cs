using System.Text.Json;

namespace PCRemote.Server.Licensing;

public enum ServerTier : byte
{
    Free = 0,
    Pro = 1,
}

/// <summary>
/// Owns the server's Free/Pro tier. Pro is unlocked by a license key sold on the website
/// (Lemon Squeezy, Merchant of Record) and activated here via Lemon Squeezy's public License API.
/// The activation result is cached under %APPDATA%/GriffinStream/license.dat so Pro survives restarts
/// and works offline within a grace window.
///
/// During closed testing <see cref="BetaFreePro"/> is true so every server reports Pro with no key
/// (frictionless for testers). Flip it to false for the paid launch build.
/// </summary>
public static class LicenseManager
{
    // TESTING FLAG: while in closed testing, all servers report Pro so testers get full features
    // with no license key. FLIP TO false for the paid launch build.
    public static readonly bool BetaFreePro = true;

    // Lemon Squeezy License API (public endpoints, no API token required for activate/validate).
    private const string ActivateUrl = "https://api.lemonsqueezy.com/v1/licenses/activate";
    private const string ValidateUrl = "https://api.lemonsqueezy.com/v1/licenses/validate";

    // How long a previously-validated license keeps working without a successful online re-check.
    private const int OfflineGraceDays = 30;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly object _gate = new();

    public static ServerTier CurrentTier { get; private set; } = ServerTier.Free;

    /// <summary>Raised whenever the tier changes (e.g. after activation) so the UI/clients can update.</summary>
    public static event Action<ServerTier>? TierChanged;

    /// <summary>Human-readable reason for the current tier, for display in the UI/logs.</summary>
    public static string StatusText { get; private set; } = "Free";

    private static string LicenseDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GriffinStream");

    private static string LicenseFile => Path.Combine(LicenseDir, "license.dat");

    private sealed class LicenseCache
    {
        public string LicenseKey { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime ActivatedUtc { get; set; }
        public DateTime LastValidatedUtc { get; set; }
    }

    /// <summary>
    /// Resolve the startup tier: beta flag wins; otherwise validate the cached license online (best
    /// effort) and fall back to the cached result within the offline grace window.
    /// </summary>
    public static async Task InitializeAsync()
    {
        if (BetaFreePro)
        {
            SetTier(ServerTier.Pro, "Pro (free during beta testing)");
            Console.WriteLine("[License] BETA_FREE_PRO is ON - reporting Pro to all clients (no key required).");
            return;
        }

        var cache = LoadCache();
        if (cache == null || string.IsNullOrWhiteSpace(cache.LicenseKey))
        {
            SetTier(ServerTier.Free, "Free");
            return;
        }

        try
        {
            var (ok, status, _) = await ValidateAsync(cache.LicenseKey, cache.InstanceId);
            if (ok && string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            {
                cache.Status = status;
                cache.LastValidatedUtc = DateTime.UtcNow;
                SaveCache(cache);
                SetTier(ServerTier.Pro, "Pro (licensed)");
                return;
            }

            // Reached the server and the license is genuinely not valid -> drop it.
            Console.WriteLine($"[License] Cached license no longer valid (status={status}). Reverting to Free.");
            ClearCache();
            SetTier(ServerTier.Free, "Free");
        }
        catch (Exception ex)
        {
            // Offline / transient: honor the cached license within the grace window.
            var age = DateTime.UtcNow - cache.LastValidatedUtc;
            if (age <= TimeSpan.FromDays(OfflineGraceDays))
            {
                SetTier(ServerTier.Pro, "Pro (offline, cached)");
                Console.WriteLine($"[License] Could not re-validate online ({ex.Message}); using cached Pro (grace).");
            }
            else
            {
                SetTier(ServerTier.Free, "Free (license needs re-validation online)");
                Console.WriteLine("[License] Offline beyond grace window; reverting to Free until re-validated.");
            }
        }
    }

    /// <summary>Activate a license key on this machine and, on success, unlock Pro and cache it.</summary>
    public static async Task<(bool ok, string message)> ActivateAsync(string licenseKey)
    {
        licenseKey = (licenseKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(licenseKey))
            return (false, "Enter a license key.");

        try
        {
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("license_key", licenseKey),
                new KeyValuePair<string, string>("instance_name", Environment.MachineName),
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, ActivateUrl) { Content = form };
            req.Headers.Add("Accept", "application/json");
            using var resp = await Http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            bool activated = root.TryGetProperty("activated", out var a) && a.ValueKind == JsonValueKind.True;
            string status = root.TryGetProperty("license_key", out var lk) &&
                            lk.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";
            string instanceId = root.TryGetProperty("instance", out var inst) &&
                                inst.ValueKind == JsonValueKind.Object &&
                                inst.TryGetProperty("id", out var id) ? (id.GetString() ?? "") : "";

            if (!activated)
            {
                string err = root.TryGetProperty("error", out var e) ? (e.GetString() ?? "") : "";
                return (false, string.IsNullOrWhiteSpace(err) ? "That key could not be activated. Check the key and your internet connection." : err);
            }

            var cache = new LicenseCache
            {
                LicenseKey = licenseKey,
                InstanceId = instanceId,
                Status = status,
                ActivatedUtc = DateTime.UtcNow,
                LastValidatedUtc = DateTime.UtcNow,
            };
            SaveCache(cache);
            SetTier(ServerTier.Pro, "Pro (licensed)");
            return (true, "Pro activated. Thank you!");
        }
        catch (Exception ex)
        {
            return (false, $"Activation failed: {ex.Message}");
        }
    }

    /// <summary>Call Lemon Squeezy validate. Returns (reachedServerAndValid, status, rawBody).</summary>
    private static async Task<(bool valid, string status, string body)> ValidateAsync(string licenseKey, string instanceId)
    {
        var pairs = new List<KeyValuePair<string, string>>
        {
            new("license_key", licenseKey),
        };
        if (!string.IsNullOrWhiteSpace(instanceId))
            pairs.Add(new("instance_id", instanceId));

        using var req = new HttpRequestMessage(HttpMethod.Post, ValidateUrl) { Content = new FormUrlEncodedContent(pairs) };
        req.Headers.Add("Accept", "application/json");
        using var resp = await Http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        bool valid = root.TryGetProperty("valid", out var v) && v.ValueKind == JsonValueKind.True;
        string status = root.TryGetProperty("license_key", out var lk) &&
                        lk.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";
        return (valid, status, body);
    }

    private static void SetTier(ServerTier tier, string status)
    {
        bool changed;
        lock (_gate)
        {
            changed = CurrentTier != tier || StatusText != status;
            CurrentTier = tier;
            StatusText = status;
        }
        if (changed)
        {
            try { TierChanged?.Invoke(tier); } catch { /* subscribers are best-effort */ }
        }
    }

    private static LicenseCache? LoadCache()
    {
        try
        {
            if (!File.Exists(LicenseFile)) return null;
            return JsonSerializer.Deserialize<LicenseCache>(File.ReadAllText(LicenseFile));
        }
        catch { return null; }
    }

    private static void SaveCache(LicenseCache cache)
    {
        try
        {
            Directory.CreateDirectory(LicenseDir);
            File.WriteAllText(LicenseFile, JsonSerializer.Serialize(cache));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[License] Failed to save license cache: {ex.Message}");
        }
    }

    private static void ClearCache()
    {
        try { if (File.Exists(LicenseFile)) File.Delete(LicenseFile); } catch { /* ignore */ }
    }
}
