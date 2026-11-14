var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.DemoFactory_Api>("factory-api")
    .WithExternalHttpEndpoints();

var orchestrator = builder.AddProject<Projects.DemoFactory_Orchestrator>("factory-orchestrator")
    .WithReference(api);

if (Directory.Exists(Path.Combine(builder.AppHostDirectory, "..", "..", "web", "node_modules")))
{
    builder.AddJavaScriptApp("factory-web", "../../web", runScriptName: "dev")
        .WithReference(api)
        .WithHttpEndpoint(port: 5173, env: "PORT")
        .WithExternalHttpEndpoints();
}

builder.Build().Run();
