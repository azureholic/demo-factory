using System.Security.Claims;
using System.Text.Json.Serialization;
using DemoFactory.Agents;
using DemoFactory.Api;
using DemoFactory.Copilot;
using DemoFactory.Domain;
using DemoFactory.GitHub;
using DemoFactory.Infrastructure;
using DemoFactory.Orchestrator;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddOpenApi();
builder.Services.AddCors(opts =>
{
    opts.AddDefaultPolicy(p => p
        .SetIsOriginAllowed(_ => true)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(opts =>
    {
        opts.Cookie.Name = "df_session";
        opts.Cookie.HttpOnly = true;
        opts.Cookie.SameSite = SameSiteMode.Lax;
        opts.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        opts.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(FactoryRoles.ViewerPolicy, p => p.RequireRole(nameof(UserRole.Viewer), nameof(UserRole.Operator), nameof(UserRole.Admin)))
    .AddPolicy(FactoryRoles.OperatorPolicy, p => p.RequireRole(nameof(UserRole.Operator), nameof(UserRole.Admin)))
    .AddPolicy(FactoryRoles.AdminPolicy, p => p.RequireRole(nameof(UserRole.Admin)));

var signalRBuilder = builder.Services.AddSignalR();
if (!string.IsNullOrWhiteSpace(builder.Configuration["Azure:SignalR:ConnectionString"]))
{
    signalRBuilder.AddAzureSignalR();
}

var isLeanProfile = !string.Equals(builder.Configuration["Factory:Profile"], nameof(DeploymentProfile.Reference), StringComparison.OrdinalIgnoreCase);
var registerBackgroundWorkers = builder.Configuration.GetValue<bool?>("Factory:RegisterBackgroundWorkers") ?? isLeanProfile;

builder.Services.AddDemoFactoryInfrastructure(builder.Configuration);
builder.Services.AddDemoFactoryGitHub(builder.Configuration);
builder.Services.AddDemoFactoryCopilot();
builder.Services.AddDemoFactoryAgents(builder.Configuration);
builder.Services.AddDemoFactoryOrchestrator(builder.Configuration, registerBackgroundWorkers: registerBackgroundWorkers);
builder.Services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();

var app = builder.Build();

// Ensure schema & seed data on startup
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DemoFactoryDbContext>>();
    var envOpts = scope.ServiceProvider.GetRequiredService<IOptions<FactoryEnvironmentOptions>>().Value;
    if (!envOpts.AllowTestHeaderAuth && string.IsNullOrWhiteSpace(envOpts.LocalDataDirectory))
    {
        envOpts.LocalDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DemoFactory");
    }

    var effectiveSandbox = envOpts.GetEffectiveSandboxMode();
    var seedDemo = effectiveSandbox && builder.Configuration.GetValue("Factory:SeedDemoData", true);
    await DemoFactorySeeder.EnsureSeededAsync(dbFactory, envOpts.OwnerLogin, seedDemo);
}

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();

// Automated test-harness header authentication (enabled strictly in WebApplicationFactory E2E tests via Factory:AllowTestHeaderAuth)
app.Use(async (ctx, next) =>
{
    var envOpts = ctx.RequestServices.GetRequiredService<IOptions<FactoryEnvironmentOptions>>().Value;
    if (envOpts.AllowTestHeaderAuth && ctx.User.Identity?.IsAuthenticated != true)
    {
        if (ctx.Request.Headers.TryGetValue("X-Test-Unauthenticated", out var unauth) && unauth == "true")
        {
            await next();
            return;
        }

        var testLogin = ctx.Request.Headers["X-Test-User"].FirstOrDefault() ?? envOpts.OwnerLogin;
        var testRole = ctx.Request.Headers["X-Test-Role"].FirstOrDefault() ?? nameof(UserRole.Admin);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, testLogin),
            new Claim(ClaimTypes.Role, testRole)
        };
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    await next();
});

app.UseAuthorization();

app.MapOpenApi("/openapi/v1.json");
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy", service = "demo-factory-api", timestamp = DateTimeOffset.UtcNow }));
app.MapGet("/readyz", () => Results.Ok(new { status = "ready" }));

app.MapFactoryAuthEndpoints();
app.MapProjectAndFeatureEndpoints();
app.MapOperationsAndGovernanceEndpoints();
app.MapHub<FactoryHub>("/hubs/factory");

if (!string.IsNullOrWhiteSpace(builder.Configuration["Factory:FrontendDevUrl"]))
{
    var devUrl = builder.Configuration["Factory:FrontendDevUrl"]!;
    app.MapGet("/", () => Results.Redirect(devUrl)).ExcludeFromDescription();
}
else if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html")))
{
    app.MapFallbackToFile("index.html");
}

app.Run();

namespace DemoFactory.Api
{
    public sealed class ApiAssemblyMarker;
}

