using DemoFactory.Agents;
using DemoFactory.Copilot;
using DemoFactory.GitHub;
using DemoFactory.Infrastructure;
using DemoFactory.Orchestrator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDemoFactoryInfrastructure(builder.Configuration);
builder.Services.AddDemoFactoryGitHub(builder.Configuration);
builder.Services.AddDemoFactoryCopilot();
builder.Services.AddDemoFactoryAgents(builder.Configuration);
builder.Services.AddDemoFactoryOrchestrator(builder.Configuration, registerBackgroundWorkers: true);

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DemoFactoryDbContext>>();
    var envOpts = scope.ServiceProvider.GetRequiredService<IOptions<FactoryEnvironmentOptions>>().Value;
    if (!envOpts.AllowTestHeaderAuth && string.IsNullOrWhiteSpace(envOpts.LocalDataDirectory))
    {
        envOpts.LocalDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DemoFactory");
    }

    await DemoFactorySeeder.EnsureSeededAsync(dbFactory, envOpts.OwnerLogin, seedDemoData: false);
}

await host.RunAsync();
