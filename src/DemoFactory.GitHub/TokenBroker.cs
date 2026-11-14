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
        else
        {
            signature = _ephemeralRsa.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        return $"{signingInput}.{Base64UrlEncode(signature)}";
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
    bool ReconnectRequired);

public interface ITokenBroker
{
    string BuildAuthorizeUrl(string state);
    Task<string> CompleteOAuthCodeExchangeAsync(string code, CancellationToken ct = default);
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
public sealed class TokenBroker(
    IDbContextFactory<DemoFactoryDbContext> dbFactory,
    ITokenProtector protector,
    IAuditLogger auditLogger,
    IHttpClientFactory httpClientFactory,
    IOptions<GitHubAppOptions> appOptions,
    IOptions<FactoryEnvironmentOptions> envOptions,
    ILogger<TokenBroker> logger) : ITokenBroker
{
    private sealed record CachedAccessToken(string AccessToken, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, CachedAccessToken> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _userLocks = new(StringComparer.OrdinalIgnoreCase);

    public string BuildAuthorizeUrl(string state)
    {
        var clientId = Uri.EscapeDataString(appOptions.Value.ClientId);
        var redirect = Uri.EscapeDataString(appOptions.Value.OAuthRedirectUri);
        return $"https://github.com/login/oauth/authorize?client_id={clientId}&redirect_uri={redirect}&state={Uri.EscapeDataString(state)}";
    }

    public async Task<string> CompleteOAuthCodeExchangeAsync(string code, CancellationToken ct = default)
    {
        if (envOptions.Value.SandboxMode || string.IsNullOrWhiteSpace(appOptions.Value.ClientSecret))
        {
            var login = envOptions.Value.OwnerLogin;
            await StoreRotatedTokensAsync(
                login,
                $"ghu_sandbox_{Guid.NewGuid():N}",
                TimeSpan.FromHours(8),
                $"ghr_sandbox_{Guid.NewGuid():N}",
                TimeSpan.FromDays(180),
                ct);
            return login;
        }

        using var http = httpClientFactory.CreateClient("GitHubOAuth");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = JsonContent.Create(new
            {
                client_id = appOptions.Value.ClientId,
                client_secret = appOptions.Value.ClientSecret,
                code,
                redirect_uri = appOptions.Value.OAuthRedirectUri
            })
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var tokenDoc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = tokenDoc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Missing access_token");
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? string.Empty : string.Empty;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 28800;
        var refreshExpiresIn = root.TryGetProperty("refresh_token_expires_in", out var rei) ? rei.GetInt32() : 15552000;

        using var userReq = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        userReq.Headers.UserAgent.ParseAdd("Demo-Factory/0.8");
        using var userResp = await http.SendAsync(userReq, ct);
        userResp.EnsureSuccessStatusCode();
        using var userDoc = await JsonDocument.ParseAsync(await userResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var loginName = userDoc.RootElement.GetProperty("login").GetString() ?? throw new InvalidOperationException("Missing GitHub login");

        await StoreRotatedTokensAsync(
            loginName,
            accessToken,
            TimeSpan.FromSeconds(expiresIn),
            refreshToken,
            TimeSpan.FromSeconds(refreshExpiresIn),
            ct);

        return loginName;
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

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var stored = await db.EncryptedUserTokens.FirstOrDefaultAsync(t => t.GitHubLogin == gitHubLogin, ct);
            if (stored is null || stored.IsRevoked || stored.RefreshTokenExpiresAt <= DateTimeOffset.UtcNow)
            {
                await HandleRevocationOrRefreshFailureInternalAsync(gitHubLogin, "Refresh token missing, revoked, or expired.", ct);
                throw new InvalidOperationException("Reconnect GitHub: delegated user token is unavailable.");
            }

            var refreshToken = await protector.DecryptRefreshTokenAsync(stored.EncryptedRefreshToken, ct);

            if (envOptions.Value.SandboxMode || string.IsNullOrWhiteSpace(appOptions.Value.ClientSecret))
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
            using var http = httpClientFactory.CreateClient("GitHubOAuth");
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
            {
                Content = JsonContent.Create(new
                {
                    client_id = appOptions.Value.ClientId,
                    client_secret = appOptions.Value.ClientSecret,
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
            var newRefresh = doc.RootElement.GetProperty("refresh_token").GetString()!;
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
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var record = await db.EncryptedUserTokens.FirstOrDefaultAsync(t => t.GitHubLogin == gitHubLogin, ct);
        if (record is null)
            return new DelegatedTokenStatus(gitHubLogin, false, false, null, null, true);

        var expired = record.RefreshTokenExpiresAt <= DateTimeOffset.UtcNow;
        var reconnect = record.IsRevoked || expired;
        return new DelegatedTokenStatus(
            gitHubLogin,
            Connected: !reconnect,
            IsRevoked: record.IsRevoked,
            AccessTokenExpiresAt: record.AccessTokenExpiresAt,
            RefreshTokenExpiresAt: record.RefreshTokenExpiresAt,
            ReconnectRequired: reconnect);
    }

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
        if (!envOptions.Value.SandboxMode
            && !string.IsNullOrWhiteSpace(appOptions.Value.ClientSecret)
            && _memoryCache.TryGetValue(gitHubLogin, out var cached))
        {
            using var http = httpClientFactory.CreateClient("GitHubOAuth");
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"https://api.github.com/applications/{appOptions.Value.ClientId}/token")
            {
                Content = JsonContent.Create(new { access_token = cached.AccessToken })
            };
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{appOptions.Value.ClientId}:{appOptions.Value.ClientSecret}"));
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
        var encrypted = await protector.EncryptRefreshTokenAsync(refreshToken, ct);
        var now = DateTimeOffset.UtcNow;
        var accessExpires = now.Add(accessLifetime);
        var refreshExpires = now.Add(refreshLifetime);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.EncryptedUserTokens.FirstOrDefaultAsync(t => t.GitHubLogin == gitHubLogin, ct);
        if (entity is null)
        {
            db.EncryptedUserTokens.Add(new EncryptedUserTokenEntity
            {
                GitHubLogin = gitHubLogin,
                EncryptedRefreshToken = encrypted,
                KeyId = string.IsNullOrWhiteSpace(appOptions.Value.KeyVaultWrapKeyId) ? "local-aes-gcm" : appOptions.Value.KeyVaultWrapKeyId,
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
        logger.LogWarning("Delegated GitHub token unavailable for {Login}: {Reason}. Pausing all projects.", gitHubLogin, reason);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
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
        await auditLogger.RecordAsync(
            gitHubLogin,
            "FactoryPausedReconnectRequired",
            "TokenBroker",
            gitHubLogin,
            after: new { reason, pausedProjects = projects.Count, pausedRepos = repos.Count },
            ct: ct);
    }
}
