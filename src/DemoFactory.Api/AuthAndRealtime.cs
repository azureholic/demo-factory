using System.Security.Claims;
using DemoFactory.Domain;
using DemoFactory.GitHub;
using DemoFactory.Infrastructure;
using DemoFactory.Orchestrator;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
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

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapFactoryAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/github/login", async (
            HttpContext http,
            ITokenBroker tokenBroker,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IOptions<FactoryEnvironmentOptions> envOptions) =>
        {
            // In local sandbox mode without live GitHub OAuth credentials, sign in the allowlisted owner directly with an HTTP-only cookie
            if (envOptions.Value.SandboxMode)
            {
                var login = await tokenBroker.CompleteOAuthCodeExchangeAsync("sandbox_code", http.RequestAborted);
                await SignInAllowlistedCookieAsync(http, dbFactory, login);
                var returnUrl = http.Request.Query["returnUrl"].FirstOrDefault() ?? "/";
                return Results.Redirect(returnUrl);
            }

            var state = Guid.NewGuid().ToString("N");
            http.Response.Cookies.Append("df_oauth_state", state, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = http.Request.IsHttps,
                Expires = DateTimeOffset.UtcNow.AddMinutes(10)
            });

            return Results.Redirect(tokenBroker.BuildAuthorizeUrl(state));
        });

        app.MapGet("/auth/github/callback", async (
            string code,
            string? state,
            HttpContext http,
            ITokenBroker tokenBroker,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IOptions<FactoryEnvironmentOptions> envOptions) =>
        {
            if (!envOptions.Value.SandboxMode)
            {
                var expected = http.Request.Cookies["df_oauth_state"];
                if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, state, StringComparison.Ordinal))
                    return Results.Unauthorized();
            }

            var login = await tokenBroker.CompleteOAuthCodeExchangeAsync(code, http.RequestAborted);
            var allowed = await SignInAllowlistedCookieAsync(http, dbFactory, login);
            if (!allowed)
                return Results.Forbid();

            return Results.Redirect("/");
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
            await using var db = await dbFactory.CreateDbContextAsync(http.RequestAborted);
            var user = await db.AllowlistUsers.FirstOrDefaultAsync(u => u.GitHubLogin == login, http.RequestAborted);
            var conn = await tokenBroker.GetConnectionStatusAsync(login, http.RequestAborted);

            return Results.Ok(new
            {
                login,
                role,
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
        string login)
    {
        await using var db = await dbFactory.CreateDbContextAsync(http.RequestAborted);
        var allow = await db.AllowlistUsers.FirstOrDefaultAsync(
            u => u.GitHubLogin.ToLower() == login.ToLower(),
            http.RequestAborted);

        if (allow is null)
            return false;

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, allow.GitHubLogin),
            new Claim(ClaimTypes.Role, allow.Role.ToString())
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });
        return true;
    }
}
