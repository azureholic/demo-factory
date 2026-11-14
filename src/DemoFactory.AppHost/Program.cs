var builder = DistributedApplication.CreateBuilder(args);

var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
var localDataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "DemoFactory");
Directory.CreateDirectory(localDataDir);

var sharedSqlitePath = Path.Combine(localDataDir, "demofactory-aspire.db");
var sharedSqliteConn = $"Data Source={sharedSqlitePath};Cache=Shared";
var policiesDir = Path.Combine(repoRoot, "policies", "repos");
var evidenceDir = Path.Combine(localDataDir, "evidence");
var promptsDir = Path.Combine(repoRoot, "agents", "pm");

var ghClientId = builder.Configuration["GitHub:ClientId"] ?? string.Empty;
var ghClientSecret = builder.Configuration["GitHub:ClientSecret"] ?? string.Empty;

var api = builder.AddProject<Projects.DemoFactory_Api>("factory-api")
    .WithHttpHealthCheck("/healthz")
    .WithEnvironment("ConnectionStrings__Sqlite", sharedSqliteConn)
    .WithEnvironment("Factory__RegisterBackgroundWorkers", "false")
    .WithEnvironment("Factory__LocalDataDirectory", localDataDir)
    .WithEnvironment("Factory__PoliciesDirectory", policiesDir)
    .WithEnvironment("Factory__EvidenceLocalDirectory", evidenceDir)
    .WithEnvironment("Factory__FrontendDevUrl", "http://localhost:5173")
    .WithEnvironment("Foundry__PromptsDirectory", promptsDir)
    .WithExternalHttpEndpoints();

var orchestrator = builder.AddProject<Projects.DemoFactory_Orchestrator>("factory-orchestrator")
    .WithReference(api)
    .WaitFor(api)
    .WithEnvironment("ConnectionStrings__Sqlite", sharedSqliteConn)
    .WithEnvironment("Factory__LocalDataDirectory", localDataDir)
    .WithEnvironment("Factory__PoliciesDirectory", policiesDir)
    .WithEnvironment("Factory__EvidenceLocalDirectory", evidenceDir)
    .WithEnvironment("Foundry__PromptsDirectory", promptsDir);

if (!string.IsNullOrWhiteSpace(ghClientId) && !string.IsNullOrWhiteSpace(ghClientSecret))
{
    api.WithEnvironment("GitHub__ClientId", ghClientId)
       .WithEnvironment("GitHub__ClientSecret", ghClientSecret);
    orchestrator.WithEnvironment("GitHub__ClientId", ghClientId)
                .WithEnvironment("GitHub__ClientSecret", ghClientSecret);
}

builder.AddViteApp("factory-web", "../../web")
    .WithNpm(install: true, installArgs: ["--legacy-peer-deps"])
    .WithReference(api)
    .WaitFor(api)
    .WithEndpoint("http", e => e.Port = 5173)
    .WithExternalHttpEndpoints();

builder.Build().Run();
