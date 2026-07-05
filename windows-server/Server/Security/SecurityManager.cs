using System.Net.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace PCRemote.Server.Security;

/// <summary>
/// A device enrolled to authenticate against this server. The public key (EC P-256 SPKI DER,
/// base64) is the identity; the label and timestamps are for the user's "Paired devices" view.
/// </summary>
public class EnrolledDevice
{
    public string Label { get; set; } = "Device";
    public string PubKeyB64 { get; set; } = "";
    public DateTime EnrolledUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public class SecurityManager
{
    private readonly X509Certificate2 _serverCertificate;

    // Enrolled devices keyed by base64(SHA-256(pubKey DER)) for O(1) lookup.
    private readonly Dictionary<string, EnrolledDevice> _authorizedKeys = new();
    private readonly object _keysLock = new();

    // Labeled key store lives beside the executable so its location is stable across launches
    // regardless of the working directory.
    private static readonly string StorePath =
        Path.Combine(AppContext.BaseDirectory, "authorized_keys.json");

    /// <summary>
    /// One-time pairing PIN for this server session. A new device enrolls by sending this PIN
    /// (shown in the server window / tray) together with its public key.
    /// </summary>
    public string PairingPin { get; }

    // Brute-force protection for PIN pairing. Shared across all client connections.
    private readonly object _pinLock = new();
    private int _failedPinAttempts = 0;
    private DateTime _pinLockoutUntil = DateTime.MinValue;
    private const int MaxPinAttempts = 5;
    private static readonly TimeSpan PinLockoutDuration = TimeSpan.FromSeconds(30);

    public SecurityManager()
    {
        _serverCertificate = LoadOrCreateCertificate();
        LoadAuthorizedKeys();
        PairingPin = GeneratePairingPin();
    }

    public async Task<Stream> EstablishSecureConnection(Stream stream, CancellationToken cancellationToken)
    {
        var sslStream = new SslStream(stream, false, ValidateClientCertificate);
        await sslStream.AuthenticateAsServerAsync(
            _serverCertificate,
            clientCertificateRequired: false,
            enabledSslProtocols: System.Security.Authentication.SslProtocols.Tls13,
            checkCertificateRevocation: false);
        return sslStream;
    }

    private bool ValidateClientCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (certificate == null)
        {
            return true; // No client certificate required (client authenticates via signed challenge).
        }
        bool isValid = sslPolicyErrors == SslPolicyErrors.None;
        if (!isValid)
        {
            Console.WriteLine($"Client certificate validation failed: {sslPolicyErrors}");
        }
        return isValid;
    }

    // ---- Challenge-response authentication -------------------------------------------------

    /// <summary>True if the given public key (SPKI DER) is enrolled.</summary>
    public bool IsEnrolled(byte[] pubKey)
    {
        var hash = KeyHash(pubKey);
        lock (_keysLock) return _authorizedKeys.ContainsKey(hash);
    }

    /// <summary>Fresh 32-byte single-use nonce for a challenge.</summary>
    public static byte[] GenerateNonce()
    {
        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);
        return nonce;
    }

    /// <summary>
    /// Verify a client's ECDSA (SHA256withECDSA, DER-encoded) signature over the challenge nonce
    /// against the supplied public key. This proves possession of the private key.
    /// </summary>
    public bool VerifyChallenge(byte[] pubKeySpki, byte[] nonce, byte[] signatureDer)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(pubKeySpki, out _);
            // Android's Signature("SHA256withECDSA") emits an ASN.1/DER (Rfc3279) sequence.
            return ecdsa.VerifyData(nonce, signatureDer, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Auth] Signature verification error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Validate a pairing PIN with brute-force lockout. Returns true only when the PIN is correct
    /// and the server is not currently locked out.
    /// </summary>
    public bool ValidatePin(string pin)
    {
        lock (_pinLock)
        {
            if (DateTime.UtcNow < _pinLockoutUntil)
            {
                int remaining = (int)Math.Ceiling((_pinLockoutUntil - DateTime.UtcNow).TotalSeconds);
                Console.WriteLine($"Pairing rejected: locked out for another {remaining}s after too many failed PIN attempts.");
                return false;
            }
        }

        if (!FixedTimeEquals(pin ?? "", PairingPin))
        {
            lock (_pinLock)
            {
                _failedPinAttempts++;
                if (_failedPinAttempts >= MaxPinAttempts)
                {
                    _pinLockoutUntil = DateTime.UtcNow + PinLockoutDuration;
                    _failedPinAttempts = 0;
                    Console.WriteLine($"Pairing failed: incorrect PIN. Too many attempts - pairing locked for {PinLockoutDuration.TotalSeconds:F0}s.");
                }
                else
                {
                    Console.WriteLine($"Pairing failed: incorrect PIN. ({MaxPinAttempts - _failedPinAttempts} attempt(s) left before lockout)");
                }
            }
            return false;
        }

        lock (_pinLock)
        {
            _failedPinAttempts = 0;
            _pinLockoutUntil = DateTime.MinValue;
        }
        return true;
    }

    // ---- Enrollment / revocation ----------------------------------------------------------

    /// <summary>Enroll (or re-label) a device by its public key and persist the store.</summary>
    public void EnrollDevice(byte[] pubKey, string label)
    {
        var hash = KeyHash(pubKey);
        lock (_keysLock)
        {
            if (_authorizedKeys.TryGetValue(hash, out var existing))
            {
                existing.LastSeenUtc = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(label)) existing.Label = label;
            }
            else
            {
                _authorizedKeys[hash] = new EnrolledDevice
                {
                    Label = string.IsNullOrWhiteSpace(label) ? "Device" : label,
                    PubKeyB64 = Convert.ToBase64String(pubKey),
                    EnrolledUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow
                };
                Console.WriteLine($"Paired new device: {label}");
            }
            SaveAuthorizedKeys();
        }
    }

    /// <summary>Update the last-seen timestamp for an authenticated device.</summary>
    public void MarkSeen(byte[] pubKey)
    {
        var hash = KeyHash(pubKey);
        lock (_keysLock)
        {
            if (_authorizedKeys.TryGetValue(hash, out var d))
            {
                d.LastSeenUtc = DateTime.UtcNow;
                SaveAuthorizedKeys();
            }
        }
    }

    /// <summary>Revoke a device by its public key. Returns true if a device was removed.</summary>
    public bool RemoveDevice(byte[] pubKey)
    {
        var hash = KeyHash(pubKey);
        lock (_keysLock)
        {
            if (_authorizedKeys.Remove(hash))
            {
                SaveAuthorizedKeys();
                Console.WriteLine("Device unpaired (key revoked).");
                return true;
            }
        }
        return false;
    }

    /// <summary>Snapshot of enrolled devices for the "Paired devices" UI.</summary>
    public IReadOnlyList<EnrolledDevice> ListDevices()
    {
        lock (_keysLock)
        {
            return _authorizedKeys.Values
                .OrderBy(d => d.EnrolledUtc)
                .Select(d => new EnrolledDevice
                {
                    Label = d.Label,
                    PubKeyB64 = d.PubKeyB64,
                    EnrolledUtc = d.EnrolledUtc,
                    LastSeenUtc = d.LastSeenUtc
                })
                .ToList();
        }
    }

    /// <summary>Revoke a device by its index in <see cref="ListDevices"/>. Returns true on success.</summary>
    public bool RemoveDeviceByIndex(int index)
    {
        lock (_keysLock)
        {
            var ordered = _authorizedKeys.OrderBy(kvp => kvp.Value.EnrolledUtc).ToList();
            if (index < 0 || index >= ordered.Count) return false;
            _authorizedKeys.Remove(ordered[index].Key);
            SaveAuthorizedKeys();
            return true;
        }
    }

    // ---- Helpers --------------------------------------------------------------------------

    private static string KeyHash(byte[] pubKey) => Convert.ToBase64String(SHA256.HashData(pubKey));

    private static string GeneratePairingPin()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        int value = BitConverter.ToInt32(bytes) & 0x7FFFFFFF;
        return (value % 1_000_000).ToString("D6");
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.ASCII.GetBytes(a);
        var bb = Encoding.ASCII.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private void LoadAuthorizedKeys()
    {
        // Clean break from the legacy line-based authorized_keys.txt: it is intentionally ignored,
        // which forces a one-time re-pair onto the hardened challenge-response scheme.
        try
        {
            if (!File.Exists(StorePath))
            {
                Console.WriteLine("No paired devices yet. Pair from the app using the PIN below.");
                return;
            }

            var json = File.ReadAllText(StorePath);
            var devices = JsonSerializer.Deserialize<List<EnrolledDevice>>(json) ?? new();
            foreach (var d in devices)
            {
                if (string.IsNullOrWhiteSpace(d.PubKeyB64)) continue;
                try
                {
                    var keyBytes = Convert.FromBase64String(d.PubKeyB64);
                    _authorizedKeys[KeyHash(keyBytes)] = d;
                }
                catch (FormatException) { /* skip corrupt entry */ }
            }
            Console.WriteLine($"Loaded {_authorizedKeys.Count} paired device(s).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: could not load {StorePath}: {ex.Message}");
        }
    }

    private void SaveAuthorizedKeys()
    {
        try
        {
            var list = _authorizedKeys.Values.OrderBy(d => d.EnrolledUtc).ToList();
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StorePath, json);
            LockDownFile(StorePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: could not persist {StorePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Restrict the key store to the current user, SYSTEM and Administrators, with inheritance
    /// disabled - so another local user cannot read enrolled public keys. Best-effort.
    /// </summary>
    private static void LockDownFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var current = WindowsIdentity.GetCurrent().User;
            if (current != null)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    current, FileSystemRights.FullControl, AccessControlType.Allow));
            }
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));

            fi.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Auth] Could not lock down key store ACL: {ex.Message}");
        }
    }

    // ---- Server certificate ---------------------------------------------------------------

    private static readonly string CertPath = Path.Combine(AppContext.BaseDirectory, "server.pfx");
    private const string CertPassword = "password";

    private const X509KeyStorageFlags CertFlags =
        X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable;

    private X509Certificate2 LoadOrCreateCertificate()
    {
        try
        {
            if (File.Exists(CertPath))
            {
                var cert = new X509Certificate2(CertPath, CertPassword, CertFlags);
                if (cert.HasPrivateKey)
                {
                    return cert;
                }
                Console.WriteLine("Existing certificate has no usable private key; recreating.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not load existing certificate, recreating: {ex.Message}");
        }

        return CreateSelfSignedCertificate();
    }

    private X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=PCRemoteServer",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                false));

        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        var pfxBytes = ephemeral.Export(X509ContentType.Pkcs12, CertPassword);
        try
        {
            File.WriteAllBytes(CertPath, pfxBytes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: could not persist server certificate to {CertPath}: {ex.Message}");
        }

        return new X509Certificate2(pfxBytes, CertPassword, CertFlags);
    }
}
