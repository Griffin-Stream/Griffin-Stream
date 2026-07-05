using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PCRemote.Server.Security;

public class SecurityManager
{
    private readonly X509Certificate2 _serverCertificate;
    private readonly Dictionary<string, byte[]> _authorizedKeys = new();
    private readonly Dictionary<string, DateTime> _activeSessions = new();
    private string _authorizedKeysPath = "authorized_keys.txt";

    /// <summary>
    /// One-time pairing PIN for this server session. A new, unauthorized device can enroll
    /// itself by sending this PIN (shown in the server window) instead of the user having to
    /// paste a public key into authorized_keys.txt by hand.
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
        // Load or create server certificate
        _serverCertificate = LoadOrCreateCertificate();
        
        // Load authorized keys (would be from config file)
        LoadAuthorizedKeys();

        // Fresh pairing PIN each time the server starts.
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
        // Since clientCertificateRequired is false, clients don't need to provide certificates
        // Accept the connection if no certificate is provided (expected)
        if (certificate == null)
        {
            Console.WriteLine("Client connected without certificate (expected)");
            return true; // No client certificate required
        }
        
        // If a certificate is provided, validate it (for future use with client cert auth)
        bool isValid = sslPolicyErrors == SslPolicyErrors.None;
        if (!isValid)
        {
            Console.WriteLine($"Client certificate validation failed: {sslPolicyErrors}");
        }
        return isValid;
    }

    public async Task<(bool success, string? sessionToken)> AuthenticateClient(byte[] authData)
    {
        // Parse authentication data
        if (authData.Length < 1) return (false, null);

        var authType = authData[0];
        bool success;
        
        if (authType == 0x01) // Key-based auth (device already enrolled)
        {
            success = await AuthenticateWithKey(authData.Skip(1).ToArray());
        }
        else if (authType == 0x02) // PIN pairing (enroll a new device)
        {
            success = TryPairWithPin(authData.Skip(1).ToArray());
        }
        else
        {
            Console.WriteLine($"Unsupported authentication type: 0x{authType:X2}");
            return (false, null);
        }

        if (success)
        {
            var sessionToken = GenerateSessionToken();
            _activeSessions[sessionToken] = DateTime.UtcNow.AddHours(24); // 24 hour session
            CleanupExpiredSessions();
            return (true, sessionToken);
        }

        return (false, null);
    }

    public bool ValidateSession(string sessionToken)
    {
        if (_activeSessions.TryGetValue(sessionToken, out var expiry))
        {
            if (expiry > DateTime.UtcNow)
            {
                return true;
            }
            else
            {
                _activeSessions.Remove(sessionToken);
            }
        }
        return false;
    }

    private string GenerateSessionToken()
    {
        var bytes = new byte[32];
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return Convert.ToBase64String(bytes);
    }

    private void CleanupExpiredSessions()
    {
        var expired = _activeSessions
            .Where(kvp => kvp.Value < DateTime.UtcNow)
            .Select(kvp => kvp.Key)
            .ToList();
        
        foreach (var token in expired)
        {
            _activeSessions.Remove(token);
        }
    }

    private Task<bool> AuthenticateWithKey(byte[] keyData)
    {
        // Verify client's public key signature
        // Simplified - would use proper ECDSA/RSA verification
        Console.WriteLine($"Authenticating with key (received {keyData.Length} bytes)");
        Console.WriteLine($"Key data (first 50 bytes): {Convert.ToBase64String(keyData.Take(50).ToArray())}...");
        
        var keyHash = Convert.ToBase64String(SHA256.HashData(keyData));
        Console.WriteLine($"Key hash: {keyHash.Substring(0, Math.Min(16, keyHash.Length))}...");
        Console.WriteLine($"Authorized keys count: {_authorizedKeys.Count}");
        
        foreach (var kvp in _authorizedKeys)
        {
            if (kvp.Value.SequenceEqual(keyData))
            {
                Console.WriteLine("Key match found! Authentication successful.");
                return Task.FromResult(true);
            }
        }
        
        Console.WriteLine("Key not found in authorized keys. Authentication failed.");
        Console.WriteLine($">>> To pair this device, enter PIN {PairingPin} in the app. <<<");
        return Task.FromResult(false);
    }

    private static string GeneratePairingPin()
    {
        // 6-digit numeric PIN from a cryptographically secure source.
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        int value = BitConverter.ToInt32(bytes) & 0x7FFFFFFF;
        return (value % 1_000_000).ToString("D6");
    }

    /// <summary>
    /// Validate a pairing request: [pinLength][pin ASCII][public key]. On a correct PIN the
    /// device's public key is enrolled (added to authorized_keys.txt) so future connections
    /// authenticate silently with key auth.
    /// </summary>
    private bool TryPairWithPin(byte[] data)
    {
        // Reject pairing attempts while locked out from prior wrong PINs (brute-force guard).
        lock (_pinLock)
        {
            if (DateTime.UtcNow < _pinLockoutUntil)
            {
                int remaining = (int)Math.Ceiling((_pinLockoutUntil - DateTime.UtcNow).TotalSeconds);
                Console.WriteLine($"Pairing rejected: locked out for another {remaining}s after too many failed PIN attempts.");
                return false;
            }
        }

        if (data.Length < 1)
        {
            Console.WriteLine("Pairing failed: malformed request.");
            return false;
        }

        int pinLength = data[0];
        if (pinLength <= 0 || data.Length < 1 + pinLength)
        {
            Console.WriteLine("Pairing failed: malformed request.");
            return false;
        }

        var pin = System.Text.Encoding.ASCII.GetString(data, 1, pinLength);
        var keyData = data.Skip(1 + pinLength).ToArray();
        if (keyData.Length == 0)
        {
            Console.WriteLine("Pairing failed: no key supplied.");
            return false;
        }

        if (!FixedTimeEquals(pin, PairingPin))
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
        EnrollKey(keyData);
        Console.WriteLine("Pairing successful: new device enrolled.");
        return true;
    }

    private void EnrollKey(byte[] keyData)
    {
        var keyHash = Convert.ToBase64String(SHA256.HashData(keyData));
        if (_authorizedKeys.ContainsKey(keyHash))
        {
            return; // Already enrolled.
        }

        _authorizedKeys[keyHash] = keyData;
        try
        {
            File.AppendAllText(_authorizedKeysPath, Convert.ToBase64String(keyData) + Environment.NewLine);
            Console.WriteLine($"Saved new authorized key to {_authorizedKeysPath}");
        }
        catch (Exception ex)
        {
            // The device is enrolled for this session even if we couldn't persist it.
            Console.WriteLine($"WARNING: could not persist authorized key to {_authorizedKeysPath}: {ex.Message}");
        }
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = System.Text.Encoding.ASCII.GetBytes(a);
        var bb = System.Text.Encoding.ASCII.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    // Anchor the certificate next to the executable so it stays stable across launches
    // regardless of the current working directory (a changing cert breaks the client's pin).
    private static readonly string CertPath = Path.Combine(AppContext.BaseDirectory, "server.pfx");
    private const string CertPassword = "password";

    // Windows' TLS stack (Schannel) cannot perform server authentication with an ephemeral
    // (in-memory) private key, so the certificate must be loaded with a PERSISTED key set.
    private const X509KeyStorageFlags CertFlags =
        X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable;

    private X509Certificate2 LoadOrCreateCertificate()
    {
        // Try to load existing certificate with a persisted key set.
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

        // The certificate produced here has an EPHEMERAL private key, which Schannel rejects.
        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        // Export to PFX and re-import with a persisted key set so it's usable for TLS server auth.
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

    private void LoadAuthorizedKeys()
    {
        // Try multiple locations: current working directory first (where dotnet run is executed),
        // then source directory (where .csproj is)
        var cwdKeysPath = Path.Combine(Directory.GetCurrentDirectory(), "authorized_keys.txt");
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var sourceKeysPath = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(appDir))) ?? appDir,
            "authorized_keys.txt");
        
        string? keysPath = null;
        if (File.Exists(cwdKeysPath))
        {
            keysPath = cwdKeysPath;
            Console.WriteLine($"Found authorized_keys.txt in current directory: {cwdKeysPath}");
        }
        else if (File.Exists(sourceKeysPath))
        {
            keysPath = sourceKeysPath;
            Console.WriteLine($"Found authorized_keys.txt in source directory: {sourceKeysPath}");
        }
        
        // Remember where to persist newly paired keys. If no file exists yet, default to the
        // current working directory so pairing can create one.
        _authorizedKeysPath = keysPath ?? cwdKeysPath;

        if (keysPath != null && File.Exists(keysPath))
        {
            var keys = File.ReadAllLines(keysPath);
            int keyCount = 0;
            foreach (var key in keys)
            {
                var trimmedKey = key.Trim();
                if (string.IsNullOrWhiteSpace(trimmedKey)) continue; // Skip empty lines
                
                try
                {
                    var keyBytes = Convert.FromBase64String(trimmedKey);
                    var keyHash = Convert.ToBase64String(SHA256.HashData(keyBytes));
                    _authorizedKeys[keyHash] = keyBytes;
                    keyCount++;
                    Console.WriteLine($"Loaded authorized key #{keyCount} (hash: {keyHash.Substring(0, Math.Min(16, keyHash.Length))}...)");
                }
                catch (FormatException ex)
                {
                    Console.WriteLine($"WARNING: Failed to parse key on line {keyCount + 1}: {ex.Message}");
                    Console.WriteLine($"Key preview: {trimmedKey.Substring(0, Math.Min(50, trimmedKey.Length))}...");
                }
            }
            Console.WriteLine($"Loaded {keyCount} authorized key(s)");
        }
        else
        {
            Console.WriteLine("WARNING: authorized_keys.txt not found. Key-based authentication will not work.");
        }
    }
}
