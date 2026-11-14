using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;
using DemoFactory.Domain;
using DemoFactory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DemoFactory.GitHub;

public sealed class GitHubAppOptions
{
    public string AppId { get; set; } = "1024001";
    public string AppSlug { get; set; } = "demo-factory";
    public long ActionsIntegrationId { get; set; } = 15368;
    public long DemoFactoryIntegrationId { get; set; } = 1024001;
    public string ClientId { get; set; } = "Iv23liDemoFactoryLocal";
    public string ClientSecret { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = "dev-webhook-secret";
    public string OAuthRedirectUri { get; set; } = "http://localhost:5180/auth/github/callback";
    public string KeyVaultSigningKeyId { get; set; } = string.Empty;
    public string KeyVaultWrapKeyId { get; set; } = string.Empty;
    public string PrivateKeyPem { get; set; } = string.Empty;
    public string TemplateProjectNodeId { get; set; } = string.Empty;
    public string LocalEncryptionPassphrase { get; set; } = "demo-factory-local-dev-key-material-32b!";
    public string ManagedIdentityClientId { get; set; } = string.Empty;
    public int WritePaceMilliseconds { get; set; } = 1000;
}

public static class WebhookSignatureVerifier
{
    public static bool VerifyHmacSha256(ReadOnlySpan<byte> payload, string? signatureHeader, string webhookSecret)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(webhookSecret))
            return false;

        const string prefix = "sha256=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(signatureHeader[prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        var computed = HMACSHA256.HashData(Encoding.UTF8.GetBytes(webhookSecret), payload);
        return CryptographicOperations.FixedTimeEquals(computed, expectedBytes);
    }
}

public interface IGitHubAppJwtSigner
{
    Task<string> CreateAppJwtAsync(CancellationToken ct = default);
}

/// <summary>
/// Signs GitHub App JWTs via a non-exportable RSA key in Azure Key Vault (§3.4, §10.1),
/// with an ephemeral RSA key fallback in local sandbox mode.
/// </summary>
public sealed class GitHubAppJwtSigner(IOptions<GitHubAppOptions> options) : IGitHubAppJwtSigner
{
    private readonly RSA _ephemeralRsa = RSA.Create(2048);

    public async Task<string> CreateAppJwtAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var headerJson = """{"alg":"RS256","typ":"JWT"}""";
        var payloadJson = JsonSerializer.Serialize(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = options.Value.AppId
        });

        var signingInput = $"{Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson))}.{Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson))}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(signingInput));

        byte[] signature;
        if (!string.IsNullOrWhiteSpace(options.Value.KeyVaultSigningKeyId)
            && Uri.TryCreate(options.Value.KeyVaultSigningKeyId, UriKind.Absolute, out var keyUri))
        {
            TokenCredential credential = string.IsNullOrWhiteSpace(options.Value.ManagedIdentityClientId)
                ? new DefaultAzureCredential()
                : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.Value.ManagedIdentityClientId));

            var crypto = new CryptographyClient(keyUri, credential);
            var signResult = await crypto.SignAsync(SignatureAlgorithm.RS256, digest, ct);
            signature = signResult.Signature;
        }
        else if (!string.IsNullOrWhiteSpace(options.Value.PrivateKeyPem))
        {
            using var pemRsa = RSA.Create();
            pemRsa.ImportFromPem(NormalizePem(options.Value.PrivateKeyPem));
            signature = pemRsa.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        else
        {
            signature = _ephemeralRsa.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string NormalizePem(string raw)
    {
        var trimmed = raw.Trim().Trim('"').Replace("\\r\\n", "\n").Replace("\\n", "\n");
        if (!trimmed.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(trimmed));
                if (decoded.Contains("-----BEGIN", StringComparison.Ordinal))
                    return decoded.Replace("\\r\\n", "\n").Replace("\\n", "\n").Trim();
            }
            catch
            {
                // Keep trimmed string if not base64-wrapped PEM
            }
        }
        return trimmed;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public interface ITokenProtector
{
    Task<string> EncryptRefreshTokenAsync(string plaintext, CancellationToken ct = default);
    Task<string> DecryptRefreshTokenAsync(string ciphertext, CancellationToken ct = default);
}

/// <summary>
/// Encrypts/decrypts the user's GitHub refresh token using Azure Key Vault RSA-OAEP-256 (§3.4, §10.1, §11.1),
/// with AES-256-GCM fallback in local dev mode.
/// </summary>
public sealed class KeyVaultTokenProtector(IOptions<GitHubAppOptions> options) : ITokenProtector
{
    public async Task<string> EncryptRefreshTokenAsync(string plaintext, CancellationToken ct = default)
    {
        var rawBytes = Encoding.UTF8.GetBytes(plaintext);

        if (!string.IsNullOrWhiteSpace(options.Value.KeyVaultWrapKeyId)
            && Uri.TryCreate(options.Value.KeyVaultWrapKeyId, UriKind.Absolute, out var keyUri))
        {
            TokenCredential credential = string.IsNullOrWhiteSpace(options.Value.ManagedIdentityClientId)
                ? new DefaultAzureCredential()
                : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.Value.ManagedIdentityClientId));

            var crypto = new CryptographyClient(keyUri, credential);
            var enc = await crypto.EncryptAsync(EncryptionAlgorithm.RsaOaep256, rawBytes, ct);
            return $"kv:{Convert.ToBase64String(enc.Ciphertext)}";
        }

        var key = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.LocalEncryptionPassphrase));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[rawBytes.Length];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, rawBytes, cipher, tag);
        return $"local:{Convert.ToBase64String(nonce)}:{Convert.ToBase64String(tag)}:{Convert.ToBase64String(cipher)}";
    }

    public async Task<string> DecryptRefreshTokenAsync(string ciphertext, CancellationToken ct = default)
    {
        if (ciphertext.StartsWith("kv-wrapped:", StringComparison.Ordinal))
            return ciphertext["kv-wrapped:".Length..];

        if (ciphertext.StartsWith("kv:", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(options.Value.KeyVaultWrapKeyId)
            && Uri.TryCreate(options.Value.KeyVaultWrapKeyId, UriKind.Absolute, out var keyUri))
        {
            TokenCredential credential = string.IsNullOrWhiteSpace(options.Value.ManagedIdentityClientId)
                ? new DefaultAzureCredential()
                : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.Value.ManagedIdentityClientId));

            var crypto = new CryptographyClient(keyUri, credential);
            var dec = await crypto.DecryptAsync(EncryptionAlgorithm.RsaOaep256, Convert.FromBase64String(ciphertext[3..]), ct);
            return Encoding.UTF8.GetString(dec.Plaintext);
        }

        if (ciphertext.StartsWith("local:", StringComparison.Ordinal))
        {
            var parts = ciphertext.Split(':');
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.LocalEncryptionPassphrase));
            var nonce = Convert.FromBase64String(parts[1]);
            var tag = Convert.FromBase64String(parts[2]);
            var cipher = Convert.FromBase64String(parts[3]);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }

        throw new CryptographicException("Unrecognized refresh-token cipher envelope.");
    }
}

public sealed record DelegatedTokenStatus(
    string GitHubLogin,
    bool Connected,
    bool IsRevoked,
    DateTimeOffset? AccessTokenExpiresAt,
    DateTimeOffset? RefreshTokenExpiresAt,
    bool ReconnectRequired,
    string TokenKind = "None");

public sealed record GitHubOAuthSessionResult(
    string Login,
    string? AvatarUrl,
    string AuthMode);

public interface ITokenBroker
{
    bool IsGitHubOAuthConfigured { get; }
    bool HasPrivateKeyConfigured { get; }
    string ActiveAppId { get; }
    string ActiveClientId { get; }
    string ActiveAppSlug { get; }
    void EnsureConfigurationLoaded();
    string BuildAuthorizeUrl(string state, string? redirectUriOverride = null);
    Task ConfigureGitHubCredentialsAsync(string clientId, string clientSecret, string? appId = null, string? appSlug = null, string? webhookSecret = null, string? privateKeyPem = null, CancellationToken ct = default);
    Task<string> CompleteManifestConversionAsync(string manifestCode, CancellationToken ct = default);
    Task<GitHubOAuthSessionResult> CompleteOAuthCodeExchangeAsync(string code, string? redirectUriOverride = null, CancellationToken ct = default);
    Task<GitHubOAuthSessionResult> CompleteSandboxSignInAsync(string login, CancellationToken ct = default);
    Task<string> GetDelegatedAccessTokenAsync(string gitHubLogin, CancellationToken ct = default);
    Task<DelegatedTokenStatus> GetConnectionStatusAsync(string gitHubLogin, CancellationToken ct = default);
    Task StoreRotatedTokensAsync(string gitHubLogin, string accessToken, TimeSpan accessLifetime, string refreshToken, TimeSpan refreshLifetime, CancellationToken ct = default);
    Task HandleRevocationOrRefreshFailureAsync(string gitHubLogin, string reason, CancellationToken ct = default);
    Task RevokeDelegatedTokenAsync(string gitHubLogin, CancellationToken ct = default);
}

/// <summary>
/// Serialized per-user TokenBroker (§10.1, §18 AC 12).
/// - Caches access tokens in memory only (8h lifetime).
/// - Persists encrypted refresh token BEFORE dropping old access token.
/// - Pauses all projects immediately when refresh fails or authorization is revoked.
/// </summary>
public sealed class TokenBroker : ITokenBroker
{
    private sealed record CachedAccessToken(string AccessToken, DateTimeOffset ExpiresAt);
    private sealed record PersistedOAuthConfig(
        string ClientId,
        string EncryptedClientSecret,
        string? AppId,
        string? AppSlug,
        string? EncryptedWebhookSecret,
        string? EncryptedPrivateKeyPem = null);

    private readonly IDbContextFactory<DemoFactoryDbContext> _dbFactory;
    private readonly ITokenProtector _protector;
    private readonly IAuditLogger _auditLogger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GitHubAppOptions _appOptions;
    private readonly FactoryEnvironmentOptions _envOptions;
    private readonly ILogger<TokenBroker> _logger;
    private readonly ConcurrentDictionary<string, CachedAccessToken> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _userLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _oauthConfigFilePath;

    public TokenBroker(
        IDbContextFactory<DemoFactoryDbContext> dbFactory,
        ITokenProtector protector,
        IAuditLogger auditLogger,
        IHttpClientFactory httpClientFactory,
        IOptions<GitHubAppOptions> appOptions,
        IOptions<FactoryEnvironmentOptions> envOptions,
        ILogger<TokenBroker> logger)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _auditLogger = auditLogger;
        _httpClientFactory = httpClientFactory;
        _appOptions = appOptions.Value;
        _envOptions = envOptions.Value;
        _logger = logger;

        var dataDir = !string.IsNullOrWhiteSpace(_envOptions.LocalDataDirectory)
            ? _envOptions.LocalDataDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DemoFactory");
        Directory.CreateDirectory(dataDir);
        _oauthConfigFilePath = Path.Combine(dataDir, "github-oauth.json");

        TryLoadPersistedOAuthConfig();
    }

    public bool IsGitHubOAuthConfigured =>
        !string.IsNullOrWhiteSpace(_appOptions.ClientId)
        && !_appOptions.ClientId.Equals("Iv23liDemoFactoryLocal", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(_appOptions.ClientSecret);

    public bool HasPrivateKeyConfigured =>
        !string.IsNullOrWhiteSpace(_appOptions.PrivateKeyPem)
        || !string.IsNullOrWhiteSpace(_appOptions.KeyVaultSigningKeyId);

    public string ActiveAppId => _appOptions.AppId;
    public string ActiveClientId => _appOptions.ClientId;
    public string ActiveAppSlug => _appOptions.AppSlug;

    public void EnsureConfigurationLoaded() => TryLoadPersistedOAuthConfig();

    public string BuildAuthorizeUrl(string state, string? redirectUriOverride = null)
    {
        TryLoadPersistedOAuthConfig();
        var clientId = Uri.EscapeDataString(_appOptions.ClientId);
        var redirect = Uri.EscapeDataString(string.IsNullOrWhiteSpace(redirectUriOverride) ? _appOptions.OAuthRedirectUri : redirectUriOverride);
        return $"https://github.com/login/oauth/authorize?client_id={clientId}&redirect_uri={redirect}&state={Uri.EscapeDataString(state)}";
    }

    public async Task ConfigureGitHubCredentialsAsync(
        string clientId,
        string clientSecret,
        string? appId = null,
        string? appSlug = null,
        string? webhookSecret = null,
        string? privateKeyPem = null,
        CancellationToken ct = default)
    {
        _appOptions.ClientId = clientId.Trim();
        _appOptions.ClientSecret = clientSecret.Trim();
        if (!string.IsNullOrWhiteSpace(appId))
        {
            _appOptions.AppId = appId.Trim();
            if (long.TryParse(_appOptions.AppId, out var parsedId))
                _appOptions.DemoFactoryIntegrationId = parsedId;
        }
        if (!string.IsNullOrWhiteSpace(appSlug))
        {
            _appOptions.AppSlug = appSlug.Trim();
            _envOptions.FactoryAppSlug = _appOptions.AppSlug;
        }
        if (!string.IsNullOrWhiteSpace(webhookSecret))
            _appOptions.WebhookSecret = webhookSecret.Trim();
        if (!string.IsNullOrWhiteSpace(privateKeyPem))
            _appOptions.PrivateKeyPem = privateKeyPem;

        var persisted = new PersistedOAuthConfig(
            _appOptions.ClientId,
            await _protector.EncryptRefreshTokenAsync(_appOptions.ClientSecret, ct),
            _appOptions.AppId,
            _appOptions.AppSlug,
            string.IsNullOrWhiteSpace(webhookSecret) ? null : await _protector.EncryptRefreshTokenAsync(_appOptions.WebhookSecret, ct),
            string.IsNullOrWhiteSpace(privateKeyPem) ? null : await _protector.EncryptRefreshTokenAsync(privateKeyPem, ct));

        await File.WriteAllTextAsync(_oauthConfigFilePath, JsonSerializer.Serialize(persisted), ct);
    }

    public async Task<string> CompleteManifestConversionAsync(string manifestCode, CancellationToken ct = default)
    {
        using var http = _httpClientFactory.CreateClient("GitHubOAuth");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/app-manifests/{Uri.EscapeDataString(manifestCode)}/conversions");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.UserAgent.ParseAdd("Demo-Factory/0.8");

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;

        var appId = root.GetProperty("id").GetRawText();
        var slug = root.TryGetProperty("slug", out var sl) && sl.ValueKind == JsonValueKind.String ? sl.GetString() ?? "demo-factory" : "demo-factory";
        var clientId = root.GetProperty("client_id").GetString() ?? throw new InvalidOperationException("Manifest conversion missing client_id");
        var clientSecret = root.GetProperty("client_secret").GetString() ?? throw new InvalidOperationException("Manifest conversion missing client_secret");
        var webhookSecret = root.TryGetProperty("webhook_secret", out var ws) && ws.ValueKind == JsonValueKind.String ? ws.GetString() : null;
        var pem = root.TryGetProperty("pem", out var pm) && pm.ValueKind == JsonValueKind.String ? pm.GetString() : null;

        await ConfigureGitHubCredentialsAsync(clientId, clientSecret, appId, slug, webhookSecret, pem, ct);
        return slug;
    }

    public async Task<GitHubOAuthSessionResult> CompleteSandboxSignInAsync(string login, CancellationToken ct = default)
    {
        var cleanLogin = string.IsNullOrWhiteSpace(login) ? _envOptions.OwnerLogin : login.Trim();
        await StoreRotatedTokensAsync(
            cleanLogin,
            $"ghu_sandbox_{Guid.NewGuid():N}",
            TimeSpan.FromHours(8),
            $"ghr_sandbox_{Guid.NewGuid():N}",
            TimeSpan.FromDays(180),
            ct);
        return new GitHubOAuthSessionResult(cleanLogin, $"https://github.com/{Uri.EscapeDataString(cleanLogin)}.png?size=64", "Sandbox");
    }

    public async Task<GitHubOAuthSessionResult> CompleteOAuthCodeExchangeAsync(string code, string? redirectUriOverride = null, CancellationToken ct = default)
    {
        TryLoadPersistedOAuthConfig();

        if (!IsGitHubOAuthConfigured)
        {
            return await CompleteSandboxSignInAsync(_envOptions.OwnerLogin, ct);
        }

        var redirectUri = string.IsNullOrWhiteSpace(redirectUriOverride) ? _appOptions.OAuthRedirectUri : redirectUriOverride;

        using var http = _httpClientFactory.CreateClient("GitHubOAuth");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = JsonContent.Create(new
            {
                client_id = _appOptions.ClientId,
                client_secret = _appOptions.ClientSecret,
                code,
                redirect_uri = redirectUri
            })
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var tokenDoc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = tokenDoc.RootElement;

        if (root.TryGetProperty("error", out var oauthErr))
        {
            var desc = root.TryGetProperty("error_description", out var ed) ? ed.GetString() : oauthErr.GetString();
            throw new InvalidOperationException($"GitHub OAuth failed: {desc}");
        }

        var accessToken = root.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Missing access_token");
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) && !string.IsNullOrWhiteSpace(rt.GetString())
            ? rt.GetString()!
            : $"non_expiring:{accessToken}";
        var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 28800;
        var refreshExpiresIn = root.TryGetProperty("refresh_token_expires_in", out var rei) ? rei.GetInt32() : 15552000;

        using var userReq = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        userReq.Headers.UserAgent.ParseAdd("Demo-Factory/0.8");
        using var userResp = await http.SendAsync(userReq, ct);
        userResp.EnsureSuccessStatusCode();
        using var userDoc = await JsonDocument.ParseAsync(await userResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var loginName = userDoc.RootElement.GetProperty("login").GetString() ?? throw new InvalidOperationException("Missing GitHub login");
        var avatarUrl = userDoc.RootElement.TryGetProperty("avatar_url", out var av) ? av.GetString() : null;

        await StoreRotatedTokensAsync(
            loginName,
            accessToken,
            TimeSpan.FromSeconds(expiresIn),
            refreshToken,
            TimeSpan.FromSeconds(refreshExpiresIn),
            ct);

        return new GitHubOAuthSessionResult(loginName, avatarUrl, "GitHub");
    }

    private void TryLoadPersistedOAuthConfig()
    {
        if (IsGitHubOAuthConfigured || !File.Exists(_oauthConfigFilePath))
            return;

        try
        {
            var json = File.ReadAllText(_oauthConfigFilePath);
            var persisted = JsonSerializer.Deserialize<PersistedOAuthConfig>(json);
            if (persisted is null || string.IsNullOrWhiteSpace(persisted.ClientId) || string.IsNullOrWhiteSpace(persisted.EncryptedClientSecret))
                return;

            _appOptions.ClientId = persisted.ClientId;
            _appOptions.ClientSecret = _protector.DecryptRefreshTokenAsync(persisted.EncryptedClientSecret).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(persisted.AppId))
            {
                _appOptions.AppId = persisted.AppId;
                if (long.TryParse(_appOptions.AppId, out var parsedId))
                    _appOptions.DemoFactoryIntegrationId = parsedId;
            }
            if (!string.IsNullOrWhiteSpace(persisted.AppSlug))
            {
                _appOptions.AppSlug = persisted.AppSlug;
                _envOptions.FactoryAppSlug = _appOptions.AppSlug;
            }
            if (!string.IsNullOrWhiteSpace(persisted.EncryptedWebhookSecret))
                _appOptions.WebhookSecret = _protector.DecryptRefreshTokenAsync(persisted.EncryptedWebhookSecret).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(persisted.EncryptedPrivateKeyPem))
                _appOptions.PrivateKeyPem = _protector.DecryptRefreshTokenAsync(persisted.EncryptedPrivateKeyPem).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read persisted GitHub OAuth config at {Path}", _oauthConfigFilePath);
        }
    }

    public async Task<string> GetDelegatedAccessTokenAsync(string gitHubLogin, CancellationToken ct = default)
    {
        if (_memoryCache.TryGetValue(gitHubLogin, out var cached)
            && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return cached.AccessToken;
        }

        var gate = _userLocks.GetOrAdd(gitHubLogin, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_memoryCache.TryGetValue(gitHubLogin, out cached)
                && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return cached.AccessToken;
            }

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var stored = await db.EncryptedUserTokens.FirstOrDefaultAsync(t => t.GitHubLogin == gitHubLogin, ct);
            if (stored is null || stored.IsRevoked || stored.RefreshTokenExpiresAt <= DateTimeOffset.UtcNow)
            {
                await HandleRevocationOrRefreshFailureInternalAsync(gitHubLogin, "Refresh token missing, revoked, or expired.", ct);
                throw new InvalidOperationException("Reconnect GitHub: delegated user token is unavailable.");
            }

            var refreshToken = await _protector.DecryptRefreshTokenAsync(stored.EncryptedRefreshToken, ct);
            if (refreshToken.StartsWith("non_expiring:", StringComparison.Ordinal))
            {
                var nonExpiringToken = refreshToken["non_expiring:".Length..];
                _memoryCache[gitHubLogin] = new CachedAccessToken(nonExpiringToken, DateTimeOffset.UtcNow.AddHours(8));
                return nonExpiringToken;
            }

            if (IsSandboxRefreshToken(refreshToken))
            {
                if (_envOptions.GetEffectiveSandboxMode())
                {
                    var sandboxAccess = $"ghu_sandbox_{Guid.NewGuid():N}";
                    var sandboxRefresh = $"ghr_sandbox_{Guid.NewGuid():N}";
                    await PersistRefreshFirstThenSwapMemoryAsync(
                        gitHubLogin,
                        sandboxAccess,
                        TimeSpan.FromHours(8),
                        sandboxRefresh,
                        TimeSpan.FromDays(180),
                        ct);
                    return sandboxAccess;
                }

                throw new InvalidOperationException("Current session uses a local sandbox token. Click 'Reconnect GitHub' to sign in with your real GitHub account before running in Live GitHub mode.");
            }

            TryLoadPersistedOAuthConfig();
            if (!IsGitHubOAuthConfigured)
            {
                var sandboxAccess = $"ghu_sandbox_{Guid.NewGuid():N}";
                var sandboxRefresh = $"ghr_sandbox_{Guid.NewGuid():N}";
                await PersistRefreshFirstThenSwapMemoryAsync(
                    gitHubLogin,
                    sandboxAccess,
                    TimeSpan.FromHours(8),
                    sandboxRefresh,
                    TimeSpan.FromDays(180),
                    ct);
                return sandboxAccess;
            }

            // Refresh via GitHub App OAuth endpoint
            using var http = _httpClientFactory.CreateClient("GitHubOAuth");
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
            {
                Content = JsonContent.Create(new
                {
                    client_id = _appOptions.ClientId,
                    client_secret = _appOptions.ClientSecret,
                    grant_type = "refresh_token",
                    refresh_token = refreshToken
                })
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                await HandleRevocationOrRefreshFailureInternalAsync(gitHubLogin, $"GitHub refresh HTTP {(int)resp.StatusCode}", ct);
                throw new InvalidOperationException("Reconnect GitHub: delegated user token refresh failed.");
            }

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var errCode = err.GetString() ?? "refresh_error";
                await HandleRevocationOrRefreshFailureInternalAsync(gitHubLogin, errCode, ct);
                throw new InvalidOperationException($"Reconnect GitHub: {errCode}");
            }

            var newAccess = doc.RootElement.GetProperty("access_token").GetString()!;
            var newRefresh = doc.RootElement.TryGetProperty("refresh_token", out var nrt) && !string.IsNullOrWhiteSpace(nrt.GetString())
                ? nrt.GetString()!
                : $"non_expiring:{newAccess}";
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 28800;
            var refreshExpiresIn = doc.RootElement.TryGetProperty("refresh_token_expires_in", out var rei) ? rei.GetInt32() : 15552000;

            await PersistRefreshFirstThenSwapMemoryAsync(
                gitHubLogin,
                newAccess,
                TimeSpan.FromSeconds(expiresIn),
                newRefresh,
                TimeSpan.FromSeconds(refreshExpiresIn),
                ct);

            return newAccess;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DelegatedTokenStatus> GetConnectionStatusAsync(string gitHubLogin, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var record = await db.EncryptedUserTokens.FirstOrDefaultAsync(t => t.GitHubLogin == gitHubLogin, ct);
        if (record is null)
            return new DelegatedTokenStatus(gitHubLogin, false, false, null, null, true);

        var expired = record.RefreshTokenExpiresAt <= DateTimeOffset.UtcNow;
        var reconnect = record.IsRevoked || expired;
        var tokenKind = "GitHub";
        try
        {
            var decrypted = await _protector.DecryptRefreshTokenAsync(record.EncryptedRefreshToken, ct);
            if (IsSandboxRefreshToken(decrypted))
                tokenKind = "Sandbox";
        }
        catch
        {
            tokenKind = "Unknown";
        }

        return new DelegatedTokenStatus(
            gitHubLogin,
            Connected: !reconnect,
            IsRevoked: record.IsRevoked,
            AccessTokenExpiresAt: record.AccessTokenExpiresAt,
            RefreshTokenExpiresAt: record.RefreshTokenExpiresAt,
            ReconnectRequired: reconnect,
            TokenKind: reconnect ? "None" : tokenKind);
    }

    private static bool IsSandboxRefreshToken(string value) =>
        value.StartsWith("ghr_sandbox_", StringComparison.Ordinal)
        || value.Contains("sandbox-refresh-token", StringComparison.OrdinalIgnoreCase);

    public async Task StoreRotatedTokensAsync(
        string gitHubLogin,
        string accessToken,
        TimeSpan accessLifetime,
        string refreshToken,
        TimeSpan refreshLifetime,
        CancellationToken ct = default)
    {
        var gate = _userLocks.GetOrAdd(gitHubLogin, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await PersistRefreshFirstThenSwapMemoryAsync(gitHubLogin, accessToken, accessLifetime, refreshToken, refreshLifetime, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task HandleRevocationOrRefreshFailureAsync(string gitHubLogin, string reason, CancellationToken ct = default) =>
        HandleRevocationOrRefreshFailureInternalAsync(gitHubLogin, reason, ct);

    public async Task RevokeDelegatedTokenAsync(string gitHubLogin, CancellationToken ct = default)
    {
        TryLoadPersistedOAuthConfig();
        if (IsGitHubOAuthConfigured && _memoryCache.TryGetValue(gitHubLogin, out var cached))
        {
            using var http = _httpClientFactory.CreateClient("GitHubOAuth");
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"https://api.github.com/applications/{_appOptions.ClientId}/token")
            {
                Content = JsonContent.Create(new { access_token = cached.AccessToken })
            };
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_appOptions.ClientId}:{_appOptions.ClientSecret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            req.Headers.UserAgent.ParseAdd("Demo-Factory/0.8");
            await http.SendAsync(req, ct);
        }

        await HandleRevocationOrRefreshFailureInternalAsync(gitHubLogin, "Operator revoked delegated token via UI.", ct);
    }

    private async Task PersistRefreshFirstThenSwapMemoryAsync(
        string gitHubLogin,
        string accessToken,
        TimeSpan accessLifetime,
        string refreshToken,
        TimeSpan refreshLifetime,
        CancellationToken ct)
    {
        // §10.1: Store the new encrypted refresh token BEFORE dropping the old access token in memory
        var encrypted = await _protector.EncryptRefreshTokenAsync(refreshToken, ct);
        var now = DateTimeOffset.UtcNow;
        var accessExpires = now.Add(accessLifetime);
        var refreshExpires = now.Add(refreshLifetime);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.EncryptedUserTokens.FirstOrDefaultAsync(t => t.GitHubLogin == gitHubLogin, ct);
        if (entity is null)
        {
            db.EncryptedUserTokens.Add(new EncryptedUserTokenEntity
            {
                GitHubLogin = gitHubLogin,
                EncryptedRefreshToken = encrypted,
                KeyId = string.IsNullOrWhiteSpace(_appOptions.KeyVaultWrapKeyId) ? "local-aes-gcm" : _appOptions.KeyVaultWrapKeyId,
                AccessTokenExpiresAt = accessExpires,
                RefreshTokenExpiresAt = refreshExpires,
                IsRevoked = false,
                UpdatedAt = now
            });
        }
        else
        {
            entity.EncryptedRefreshToken = encrypted;
            entity.AccessTokenExpiresAt = accessExpires;
            entity.RefreshTokenExpiresAt = refreshExpires;
            entity.IsRevoked = false;
            entity.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        _memoryCache[gitHubLogin] = new CachedAccessToken(accessToken, accessExpires);
    }

    private async Task HandleRevocationOrRefreshFailureInternalAsync(string gitHubLogin, string reason, CancellationToken ct)
    {
        _memoryCache.TryRemove(gitHubLogin, out _);
        _logger.LogWarning("Delegated GitHub token unavailable for {Login}: {Reason}. Pausing all projects.", gitHubLogin, reason);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var token = await db.EncryptedUserTokens.FirstOrDefaultAsync(t => t.GitHubLogin == gitHubLogin, ct);
        if (token is not null)
        {
            token.IsRevoked = true;
            token.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var projects = await db.Projects.Where(p => p.State == ProjectState.Running).ToListAsync(ct);
        foreach (var project in projects)
        {
            project.State = ProjectState.Paused;
            project.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var repos = await db.Repositories.ToListAsync(ct);
        foreach (var repo in repos)
        {
            repo.Paused = true;
            repo.PauseReason = $"Reconnect GitHub ({reason})";
        }

        var globalSettings = await db.RuntimeSettings.FirstOrDefaultAsync(s => s.Scope == SettingsScope.Global, ct);
        if (globalSettings is not null)
        {
            globalSettings.GlobalPaused = true;
            globalSettings.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        await _auditLogger.RecordAsync(
            gitHubLogin,
            "FactoryPausedReconnectRequired",
            "TokenBroker",
            gitHubLogin,
            after: new { reason, pausedProjects = projects.Count, pausedRepos = repos.Count },
            ct: ct);
    }
}
