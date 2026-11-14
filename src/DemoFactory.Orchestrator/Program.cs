using DemoFactory.Agents;
using DemoFactory.Copilot;
using DemoFactory.GitHub;
using DemoFactory.Infrastructure;
using DemoFactory.Orchestrator;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDemoFactoryInfrastructure(builder.Configuration);
builder.Services.AddDemoFactoryGitHub(builder.Configuration);
builder.Services.AddDemoFactoryCopilot();
builder.Services.AddDemoFactoryAgents(builder.Configuration);
builder.Services.AddDemoFactoryOrchestrator(builder.Configuration, registerBackgroundWorkers: true);

var host = builder.Build();
await host.RunAsync();
