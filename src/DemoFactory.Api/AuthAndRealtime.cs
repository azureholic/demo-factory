using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using DemoFactory.Domain;
using DemoFactory.GitHub;
using DemoFactory.Infrastructure;
using DemoFactory.Orchestrator;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DemoFactory.Api;

public static class FactoryRoles
{
    public const string ViewerPolicy = "ViewerPolicy";
    public const string OperatorPolicy = "OperatorPolicy";
    public const string AdminPolicy = "AdminPolicy";
}

public sealed class FactoryHub : Hub
{
    public async Task JoinRepositoryGroup(string repoId) =>
        await Groups.AddToGroupAsync(Context.ConnectionId, $"repo:{repoId}");
}

public sealed class SignalRRealtimeNotifier(IHubContext<FactoryHub> hubContext) : IRealtimeNotifier
{
    public Task BroadcastWorkItemChangedAsync(Guid workItemId, Guid repoId, string state, CancellationToken ct = default) =>
        hubContext.Clients.All.SendAsync("workItemChanged", new { workItemId, repoId, state, at = DateTimeOffset.UtcNow }, ct);

    public Task BroadcastSessionEventAsync(Guid sessionId, Guid workItemId, string state, CancellationToken ct = default) =>
        hubContext.Clients.All.SendAsync("sessionEvent", new { sessionId, workItemId, state, at = DateTimeOffset.UtcNow }, ct);

    public Task BroadcastGateEvaluatedAsync(Guid workItemId, string headSha, string outcome, CancellationToken ct = default) =>
        hubContext.Clients.All.SendAsync("gateEvaluated", new { workItemId, headSha, outcome, at = DateTimeOffset.UtcNow }, ct);

    public Task BroadcastApprovalRequestedAsync(Guid approvalId, Guid workItemId, string kind, CancellationToken ct = default) =>
        hubContext.Clients.All.SendAsync("approvalRequested", new { approvalId, workItemId, kind, at = DateTimeOffset.UtcNow }, ct);

    public Task BroadcastLineStateChangedAsync(Guid repoId, string status, string? cause, CancellationToken ct = default) =>
        hubContext.Clients.All.SendAsync("lineStateChanged", new { repoId, status, cause, at = DateTimeOffset.UtcNow }, ct);

    public Task BroadcastFactoryStatusAsync(bool paused, string? reason, CancellationToken ct = default) =>
        hubContext.Clients.All.SendAsync("factoryStatus", new { paused, reason, at = DateTimeOffset.UtcNow }, ct);
}

public sealed record UserPreferencesUpdateRequest(string? ThemePreference, string? DensityPreference);
public sealed record ConfigureGitHubOAuthRequest(string ClientId, string ClientSecret, string? AppId = null, string? AppSlug = null);
public sealed record SandboxLoginRequest(string? Login = null, UserRole Role = UserRole.Admin);

public static class AuthEndpoints
{
    private const string AvatarClaimType = "urn:demofactory:avatar_url";
    private const string AuthModeClaimType = "urn:demofactory:auth_mode";
    private static readonly ConcurrentDictionary<string, DateTimeOffset> PendingManifestStates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, (string CallbackUri, string ReturnUrl, DateTimeOffset ExpiresAt)> PendingOAuthStates = new(StringComparer.Ordinal);

    public static IEndpointRouteBuilder MapFactoryAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/config", (
            HttpContext http,
            ITokenBroker tokenBroker,
            IWebHostEnvironment hostEnv,
            IOptions<FactoryEnvironmentOptions> envOptions) =>
        {
            var baseUrl = ResolvePublicBaseUrl(http);
            return Results.Ok(new
            {
                gitHubConfigured = tokenBroker.IsGitHubOAuthConfigured,
                clientId = tokenBroker.IsGitHubOAuthConfigured ? tokenBroker.ActiveClientId : string.Empty,
                appSlug = tokenBroker.ActiveAppSlug,
                sandboxAvailable = envOptions.Value.SandboxMode || hostEnv.IsDevelopment(),
                defaultOwnerLogin = envOptions.Value.OwnerLogin,
                callbackUrl = $"{baseUrl}/auth/github/callback"
            });
        });

        app.MapPost("/auth/config/github", async (
            ConfigureGitHubOAuthRequest req,
            HttpContext http,
            ITokenBroker tokenBroker,
            IWebHostEnvironment hostEnv,
            IAuditLogger auditLogger) =>
        {
            var isDevBootstrap = hostEnv.IsDevelopment() && !tokenBroker.IsGitHubOAuthConfigured;
            var isAdmin = http.User.IsInRole(nameof(UserRole.Admin));
            if (!isDevBootstrap && !isAdmin)
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(req.ClientId) || string.IsNullOrWhiteSpace(req.ClientSecret))
                return Results.BadRequest(new { error = "Both GitHub Client ID and Client Secret are required." });

            await tokenBroker.ConfigureGitHubCredentialsAsync(req.ClientId, req.ClientSecret, req.AppId, req.AppSlug, ct: http.RequestAborted);
            await auditLogger.RecordAsync(http.User.Identity?.Name ?? "setup", "GitHubOAuthConfigured", "Auth", req.ClientId.Trim(), ct: http.RequestAborted);

            return Results.Ok(new
            {
                gitHubConfigured = tokenBroker.IsGitHubOAuthConfigured,
                clientId = tokenBroker.ActiveClientId,
                appSlug = tokenBroker.ActiveAppSlug
            });
        });

        app.MapGet("/auth/github/manifest-start", (
            HttpContext http,
            IWebHostEnvironment hostEnv,
            ITokenBroker tokenBroker) =>
        {
            if (!hostEnv.IsDevelopment() && !http.User.IsInRole(nameof(UserRole.Admin)) && tokenBroker.IsGitHubOAuthConfigured)
                return Results.Forbid();

            var baseUrl = ResolvePublicBaseUrl(http);
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            PendingManifestStates[state] = DateTimeOffset.UtcNow.AddHours(1);

            http.Response.Cookies.Append("df_manifest_state", state, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = http.Request.IsHttps,
                Expires = DateTimeOffset.UtcNow.AddHours(1)
            });

            var manifest = BuildGitHubAppManifestJson(baseUrl);

            var encodedManifest = WebUtility.HtmlEncode(manifest);
            var actionUrl = $"https://github.com/settings/apps/new?state={Uri.EscapeDataString(state)}";
            var html = $"""
                <!doctype html>
                <html lang="en">
                <head><meta charset="utf-8" /><title>Redirecting to GitHub App Setup…</title></head>
                <body style="font-family: system-ui, sans-serif; padding: 40px; background: #11151c; color: #f5f5f5;">
                  <p>Redirecting to GitHub to register your personal Demo-Factory App…</p>
                  <form id="mf" action="{actionUrl}" method="post">
                    <input type="hidden" name="manifest" value="{encodedManifest}" />
                    <noscript><button type="submit">Continue to GitHub</button></noscript>
                  </form>
                  <script>document.getElementById('mf').submit();</script>
                </body>
                </html>
                """;

            return Results.Content(html, "text/html");
        });

        app.MapGet("/auth/github/manifest-callback", async (
            string? code,
            string? state,
            HttpContext http,
            ITokenBroker tokenBroker,
            IAuditLogger auditLogger) =>
        {
            if (string.IsNullOrWhiteSpace(code))
                return Results.Redirect("/?authError=Missing+GitHub+App+manifest+code");

            var validServerNonce = !string.IsNullOrWhiteSpace(state)
                && PendingManifestStates.TryRemove(state, out var exp)
                && exp > DateTimeOffset.UtcNow;

            var cookieState = http.Request.Cookies["df_manifest_state"];
            var validCookieNonce = !string.IsNullOrWhiteSpace(cookieState)
                && string.Equals(cookieState, state, StringComparison.Ordinal);

            if (!validServerNonce && !validCookieNonce)
                return Results.Redirect("/?authError=Invalid+or+expired+manifest+state");

            http.Response.Cookies.Delete("df_manifest_state");
            try
            {
                var slug = await tokenBroker.CompleteManifestConversionAsync(code, http.RequestAborted);
                await auditLogger.RecordAsync("github-manifest", "GitHubAppCreatedFromManifest", "Auth", slug, ct: http.RequestAborted);
                return Results.Redirect("/auth/github/login");
            }
            catch (Exception ex)
            {
                return Results.Redirect($"/?authError={Uri.EscapeDataString(ex.Message)}");
            }
        });

        app.MapPost("/auth/sandbox/login", async (
            SandboxLoginRequest? req,
            HttpContext http,
            ITokenBroker tokenBroker,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IWebHostEnvironment hostEnv,
            IOptions<FactoryEnvironmentOptions> envOptions,
            IAuditLogger auditLogger) =>
        {
            if (!envOptions.Value.SandboxMode && !hostEnv.IsDevelopment())
                return Results.Forbid();

            var requestedLogin = string.IsNullOrWhiteSpace(req?.Login) ? envOptions.Value.OwnerLogin : req.Login.Trim();
            var requestedRole = req?.Role ?? UserRole.Admin;

            await using (var db = await dbFactory.CreateDbContextAsync(http.RequestAborted))
            {
                var user = await db.AllowlistUsers.FirstOrDefaultAsync(u => u.GitHubLogin.ToLower() == requestedLogin.ToLower(), http.RequestAborted);
                if (user is null)
                {
                    db.AllowlistUsers.Add(new AllowlistUserEntity
                    {
                        GitHubLogin = requestedLogin,
                        Role = requestedRole,
                        CopilotPlan = "Copilot Pro+"
                    });
                }
                else
                {
                    user.Role = requestedRole;
                }
                await db.SaveChangesAsync(http.RequestAborted);
            }

            var session = await tokenBroker.CompleteSandboxSignInAsync(requestedLogin, http.RequestAborted);
            await SignInAllowlistedCookieAsync(http, dbFactory, session.Login, session.AvatarUrl, session.AuthMode, autoEnrollInDev: true);
            await auditLogger.RecordAsync(session.Login, "UserSignedInSandbox", "Auth", session.Login, after: new { role = requestedRole.ToString() }, ct: http.RequestAborted);

            return Results.Ok(new { signedIn = true, login = session.Login, role = requestedRole.ToString(), authMode = session.AuthMode });
        });

        app.MapGet("/auth/github/login", async (
            HttpContext http,
            ITokenBroker tokenBroker,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IOptions<FactoryEnvironmentOptions> envOptions) =>
        {
            var returnUrl = http.Request.Query["returnUrl"].FirstOrDefault() ?? "/";

            if (!tokenBroker.IsGitHubOAuthConfigured)
            {
                if (envOptions.Value.SandboxMode)
                {
                    var sandboxSession = await tokenBroker.CompleteSandboxSignInAsync(envOptions.Value.OwnerLogin, http.RequestAborted);
                    await SignInAllowlistedCookieAsync(http, dbFactory, sandboxSession.Login, sandboxSession.AvatarUrl, sandboxSession.AuthMode, autoEnrollInDev: true);
                    return Results.Redirect(returnUrl);
                }

                return Results.Redirect("/?authError=GitHub+OAuth+is+not+configured");
            }

            var callbackUri = $"{ResolvePublicBaseUrl(http)}/auth/github/callback";
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            PendingOAuthStates[state] = (callbackUri, returnUrl, DateTimeOffset.UtcNow.AddMinutes(15));

            var cookieOpts = new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = http.Request.IsHttps,
                Expires = DateTimeOffset.UtcNow.AddMinutes(15)
            };
            http.Response.Cookies.Append("df_oauth_state", state, cookieOpts);
            http.Response.Cookies.Append("df_oauth_callback", callbackUri, cookieOpts);
            http.Response.Cookies.Append("df_oauth_return", returnUrl, cookieOpts);

            return Results.Redirect(tokenBroker.BuildAuthorizeUrl(state, callbackUri));
        });

        app.MapGet("/auth/github/callback", async (
            string? code,
            string? state,
            string? error,
            string? error_description,
            HttpContext http,
            ITokenBroker tokenBroker,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IWebHostEnvironment hostEnv,
            IAuditLogger auditLogger) =>
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                var msg = Uri.EscapeDataString(error_description ?? error);
                return Results.Redirect($"/?authError={msg}");
            }

            if (string.IsNullOrWhiteSpace(code))
                return Results.Redirect("/?authError=Missing+authorization+code");

            string? callbackUri = null;
            string? returnUrl = null;

            if (tokenBroker.IsGitHubOAuthConfigured)
            {
                if (!string.IsNullOrWhiteSpace(state)
                    && PendingOAuthStates.TryRemove(state, out var entry)
                    && entry.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    callbackUri = entry.CallbackUri;
                    returnUrl = entry.ReturnUrl;
                }
                else
                {
                    var expected = http.Request.Cookies["df_oauth_state"];
                    if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, state, StringComparison.Ordinal))
                        return Results.Redirect("/?authError=Invalid+OAuth+state");
                }
            }

            callbackUri ??= http.Request.Cookies["df_oauth_callback"] ?? $"{ResolvePublicBaseUrl(http)}/auth/github/callback";
            returnUrl ??= http.Request.Cookies["df_oauth_return"] ?? "/";
            http.Response.Cookies.Delete("df_oauth_state");
            http.Response.Cookies.Delete("df_oauth_callback");
            http.Response.Cookies.Delete("df_oauth_return");

            try
            {
                var session = await tokenBroker.CompleteOAuthCodeExchangeAsync(code, callbackUri, http.RequestAborted);
                var allowed = await SignInAllowlistedCookieAsync(
                    http,
                    dbFactory,
                    session.Login,
                    session.AvatarUrl,
                    session.AuthMode,
                    autoEnrollInDev: hostEnv.IsDevelopment());

                if (!allowed)
                    return Results.Redirect("/?authError=Account+is+not+on+the+Demo-Factory+allowlist");

                await auditLogger.RecordAsync(session.Login, "UserSignedInGitHub", "Auth", session.Login, ct: http.RequestAborted);
                return Results.Redirect(returnUrl);
            }
            catch (Exception ex)
            {
                return Results.Redirect($"/?authError={Uri.EscapeDataString(ex.Message)}");
            }
        });

        app.MapGet("/auth/me", async (
            HttpContext http,
            ITokenBroker tokenBroker,
            IDbContextFactory<DemoFactoryDbContext> dbFactory) =>
        {
            if (http.User.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();

            var login = http.User.Identity.Name!;
            var role = http.User.FindFirstValue(ClaimTypes.Role) ?? nameof(UserRole.Viewer);
            var avatarUrl = http.User.FindFirstValue(AvatarClaimType) ?? $"https://github.com/{Uri.EscapeDataString(login)}.png?size=64";
            var authMode = http.User.FindFirstValue(AuthModeClaimType) ?? (tokenBroker.IsGitHubOAuthConfigured ? "GitHub" : "Sandbox");

            await using var db = await dbFactory.CreateDbContextAsync(http.RequestAborted);
            var user = await db.AllowlistUsers.FirstOrDefaultAsync(u => u.GitHubLogin.ToLower() == login.ToLower(), http.RequestAborted);
            var conn = await tokenBroker.GetConnectionStatusAsync(login, http.RequestAborted);

            return Results.Ok(new
            {
                login,
                role,
                avatarUrl,
                authMode,
                themePreference = user?.ThemePreference ?? "system",
                densityPreference = user?.DensityPreference ?? "comfortable",
                copilotPlan = user?.CopilotPlan ?? "Copilot Pro+",
                connection = conn
            });
        });

        app.MapPut("/auth/preferences", async (
            UserPreferencesUpdateRequest req,
            HttpContext http,
            IDbContextFactory<DemoFactoryDbContext> dbFactory) =>
        {
            var login = http.User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(login))
                return Results.Unauthorized();

            await using var db = await dbFactory.CreateDbContextAsync(http.RequestAborted);
            var user = await db.AllowlistUsers.FirstOrDefaultAsync(u => u.GitHubLogin == login, http.RequestAborted);
            if (user is not null)
            {
                if (!string.IsNullOrWhiteSpace(req.ThemePreference))
                    user.ThemePreference = req.ThemePreference;
                if (!string.IsNullOrWhiteSpace(req.DensityPreference))
                    user.DensityPreference = req.DensityPreference;
                await db.SaveChangesAsync(http.RequestAborted);
            }

            return Results.Ok(new { user?.ThemePreference, user?.DensityPreference });
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { signedOut = true });
        });

        app.MapPost("/auth/revoke", async (
            HttpContext http,
            ITokenBroker tokenBroker,
            IRealtimeNotifier notifier) =>
        {
            var login = http.User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(login))
                return Results.Unauthorized();

            await tokenBroker.RevokeDelegatedTokenAsync(login, http.RequestAborted);
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            await notifier.BroadcastFactoryStatusAsync(true, "Delegated GitHub token revoked by user.", http.RequestAborted);
            return Results.Ok(new { revoked = true, paused = true });
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        return app;
    }

    public static async Task<bool> SignInAllowlistedCookieAsync(
        HttpContext http,
        IDbContextFactory<DemoFactoryDbContext> dbFactory,
        string login,
        string? avatarUrl = null,
        string authMode = "GitHub",
        bool autoEnrollInDev = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync(http.RequestAborted);
        var allow = await db.AllowlistUsers.FirstOrDefaultAsync(
            u => u.GitHubLogin.ToLower() == login.ToLower(),
            http.RequestAborted);

        if (allow is null)
        {
            if (!autoEnrollInDev)
                return false;

            allow = new AllowlistUserEntity
            {
                GitHubLogin = login,
                Role = UserRole.Admin,
                CopilotPlan = "Copilot Pro+"
            };
            db.AllowlistUsers.Add(allow);
            await db.SaveChangesAsync(http.RequestAborted);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, allow.GitHubLogin),
            new(ClaimTypes.Role, allow.Role.ToString()),
            new(AuthModeClaimType, authMode)
        };
        if (!string.IsNullOrWhiteSpace(avatarUrl))
            claims.Add(new Claim(AvatarClaimType, avatarUrl));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });
        return true;
    }

    public static string BuildGitHubAppManifestJson(string baseUrl)
    {
        var isLocalhost = baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || baseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);

        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        return JsonSerializer.Serialize(new
        {
            name = $"demo-factory-{suffix}",
            url = isLocalhost ? "https://github.com" : baseUrl,
            hook_attributes = new
            {
                url = isLocalhost ? "https://example.com/webhooks/github" : $"{baseUrl}/webhooks/github",
                active = !isLocalhost
            },
            redirect_url = $"{baseUrl}/auth/github/manifest-callback",
            callback_urls = new[]
            {
                $"{baseUrl}/auth/github/callback",
                "http://localhost:5173/auth/github/callback",
                "http://localhost:5180/auth/github/callback"
            }.Distinct().ToArray(),
            request_oauth_on_install = true,
            @public = false,
            description = "Demo-Factory personal GitHub App for delegated OAuth sign-in and orchestration.",
            default_permissions = new Dictionary<string, string>
            {
                ["administration"] = "write",
                ["contents"] = "write",
                ["pull_requests"] = "write",
                ["issues"] = "write",
                ["checks"] = "write",
                ["actions"] = "read",
                ["vulnerability_alerts"] = "read",
                ["secret_scanning_alerts"] = "read",
                ["security_events"] = "write",
                ["metadata"] = "read"
            },
            default_events = new[]
            {
                "issues",
                "issue_comment",
                "pull_request",
                "pull_request_review",
                "pull_request_review_comment",
                "check_suite",
                "check_run",
                "workflow_run",
                "push",
                "code_scanning_alert",
                "secret_scanning_alert",
                "dependabot_alert",
                "repository"
            }
        });
    }

    private static string ResolvePublicBaseUrl(HttpContext http)
    {
        var forwardedHost = http.Request.Headers["X-Forwarded-Host"].FirstOrDefault();
        var forwardedProto = http.Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
        var scheme = !string.IsNullOrWhiteSpace(forwardedProto) ? forwardedProto.Split(',')[0].Trim() : http.Request.Scheme;
        var host = !string.IsNullOrWhiteSpace(forwardedHost) ? forwardedHost.Split(',')[0].Trim() : http.Request.Host.Value;
        return $"{scheme}://{host}";
    }
}
