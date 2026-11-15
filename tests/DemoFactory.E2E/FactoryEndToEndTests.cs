using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DemoFactory.Api;
using DemoFactory.Domain;
using DemoFactory.GitHub;
using DemoFactory.Infrastructure;
using DemoFactory.Orchestrator;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DemoFactory.E2E;

public sealed class FactoryEndToEndTests : IAsyncLifetime
{
    private readonly string _dbFile = Path.Combine(Path.GetTempPath(), $"demofactory-e2e-{Guid.NewGuid():N}.db");
    private readonly string _policiesDir = Path.Combine(Path.GetTempPath(), $"demofactory-policies-{Guid.NewGuid():N}");
    private WebApplicationFactory<ApiAssemblyMarker> _factory = null!;
    private HttpClient _client = null!;

    private const string WebhookSecret = "e2e-test-webhook-secret";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_policiesDir);

        _factory = new WebApplicationFactory<ApiAssemblyMarker>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Sqlite"] = $"Data Source={_dbFile}",
                    ["Factory:SandboxMode"] = "true",
                    ["Factory:AllowTestHeaderAuth"] = "true",
                    ["Factory:SeedDemoData"] = "true",
                    ["Factory:OwnerLogin"] = "azureholic",
                    ["Factory:PoliciesDirectory"] = _policiesDir,
                    ["Factory:LocalDataDirectory"] = _policiesDir,
                    ["Foundry:ProjectEndpoint"] = "",
                    ["GitHub:WebhookSecret"] = WebhookSecret
                });
            });
        });

        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        if (File.Exists(_dbFile))
        {
            try { File.Delete(_dbFile); } catch { }
        }
        if (Directory.Exists(_policiesDir))
        {
            try { Directory.Delete(_policiesDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EndToEnd_NewProject_Bootstrap_And_AddFeature_Without_Disturbing_Running_Work()
    {
        // 1. Create new project (§5.1 Step 1–2)
        var createResp = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = "Fabrikam Solar Microgrid",
            specMarkdown = "# Solar Microgrid\n- Display inverter telemetry cards\n- Expose GET /api/inverters",
            templateId = "dotnet-react",
            autonomy = "L1",
            codingModel = "gemini-4-carbon"
        });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var createdDoc = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = createdDoc.GetProperty("project").GetProperty("id").GetGuid();

        // 2. Clarify & generate plan (§5.1 Step 3)
        var clarifyResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}:clarify", new
        {
            answers = new Dictionary<string, string> { ["Persona"] = "Anonymous synthetic operator" }
        });
        Assert.Equal(HttpStatusCode.OK, clarifyResp.StatusCode);

        // 3. Approve plan & run deterministic bootstrap (§5.1 Steps 5–11, §18 AC 10)
        var approveResp = await _client.PostAsync($"/api/projects/{projectId}:approve", null);
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

        var repos = await _client.GetFromJsonAsync<JsonElement>("/api/repos");
        var solarRepo = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "fabrikam-solar-microgrid");
        var repoId = solarRepo.GetProperty("id").GetGuid();

        // Verify walking skeleton (#1) was automatically picked up & assigned to Copilot (Implementing / InProgress)
        var boardBefore = await _client.GetFromJsonAsync<JsonElement>($"/api/repos/{repoId}/board");
        var cardsBefore = boardBefore.GetProperty("cards").EnumerateArray().ToList();
        Assert.Contains(cardsBefore, c => c.GetProperty("state").GetString() == "Implementing");
        var inFlightId = cardsBefore.First(c => c.GetProperty("state").GetString() == "Implementing").GetProperty("id").GetGuid();

        // 4. Add Feature to running project (§5.5, §18 AC 23–24)
        var featCreate = await _client.PostAsJsonAsync($"/api/projects/{projectId}/features", new
        {
            title = "Battery Forecast Export",
            specMarkdown = "- Export 48h battery SOC forecast CSV"
        });
        Assert.Equal(HttpStatusCode.Created, featCreate.StatusCode);
        var featDoc = await featCreate.Content.ReadFromJsonAsync<JsonElement>();
        var featureId = featDoc.GetProperty("feature").GetProperty("id").GetGuid();

        var featClarify = await _client.PostAsJsonAsync($"/api/features/{featureId}:clarify", new { dismissPublicSafeFlags = true });
        Assert.Equal(HttpStatusCode.OK, featClarify.StatusCode);

        var featApprove = await _client.PostAsync($"/api/features/{featureId}:approve", null);
        Assert.Equal(HttpStatusCode.OK, featApprove.StatusCode);

        // Verify new backlog items are on the board AND the previously running item was NOT disturbed
        var boardAfter = await _client.GetFromJsonAsync<JsonElement>($"/api/repos/{repoId}/board");
        var cardsAfter = boardAfter.GetProperty("cards").EnumerateArray().ToList();
        Assert.True(cardsAfter.Count > cardsBefore.Count);
        var stillRunning = cardsAfter.First(c => c.GetProperty("id").GetGuid() == inFlightId);
        Assert.Equal("Implementing", stillRunning.GetProperty("state").GetString());
    }

    [Fact]
    public async Task EndToEnd_Webhooks_Enforce_Hmac_Deduplication_Inbox_Rule_And_ForkPr_Rejection()
    {
        // Reject invalid HMAC signature
        var badReq = new HttpRequestMessage(HttpMethod.Post, "/webhooks/github")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        badReq.Headers.Add("X-Hub-Signature-256", "sha256=deadbeef");
        badReq.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());
        badReq.Headers.Add("X-GitHub-Event", "ping");
        var badResp = await _client.SendAsync(badReq);
        Assert.Equal(HttpStatusCode.Unauthorized, badResp.StatusCode);

        // Valid outside-contributor issue webhook WITH 'factory' label applied by issue form -> MUST go to Issue Inbox, NEVER scheduled (§18 AC 25–27)
        var issuePayload = JsonSerializer.Serialize(new
        {
            action = "opened",
            sender = new { login = "external-reporter" },
            repository = new { full_name = "azureholic/contoso-smart-grid" },
            issue = new
            {
                number = 77,
                title = "Please add dark-mode export PDF button",
                body = "Would love PDF export for substation summary.",
                user = new { login = "external-reporter" },
                labels = new[] { new { name = "factory" } }
            }
        });

        var deliveryId = Guid.NewGuid().ToString();
        var issueResp = await SendSignedWebhookAsync("issues", deliveryId, issuePayload);
        Assert.Equal(HttpStatusCode.Accepted, issueResp.StatusCode);

        // Re-sending identical delivery ID is de-duplicated
        var dupResp = await SendSignedWebhookAsync("issues", deliveryId, issuePayload);
        var dupDoc = await dupResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(dupDoc.GetProperty("deduplicated").GetBoolean());

        // Verify issue #77 is in Issue Inbox, NOT on managed board
        var projects = await _client.GetFromJsonAsync<JsonElement>("/api/projects");
        var contosoProjId = projects.EnumerateArray().First(p => p.GetProperty("slug").GetString() == "contoso-smart-grid").GetProperty("id").GetGuid();

        var inbox = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{contosoProjId}/inbox");
        Assert.Contains(inbox.EnumerateArray(), i => i.GetProperty("issueNumber").GetInt32() == 77 && !string.IsNullOrWhiteSpace(i.GetProperty("suggestedGroup").GetString()));
        Assert.DoesNotContain(inbox.EnumerateArray(), i => i.GetProperty("state").GetString() == "Planned");

        // Turn inbox issue #77 into a vetted feature draft (§5.6, AC 27) -> #77 must immediately disappear from Issue Inbox!
        var fromIssuesResp = await _client.PostAsJsonAsync($"/api/projects/{contosoProjId}/features:from-issues", new
        {
            issueNumbers = new[] { 77 }
        });
        Assert.Equal(HttpStatusCode.Created, fromIssuesResp.StatusCode);

        var inboxAfterSpec = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{contosoProjId}/inbox");
        Assert.DoesNotContain(inboxAfterSpec.EnumerateArray(), i => i.GetProperty("issueNumber").GetInt32() == 77);

        // Even when the repository owner (@azureholic) manually creates an issue on GitHub and manually applies 'factory',
        // it must NOT be treated as a vetted board WorkItem — it stays in Issue Inbox until synthesized into a Feature spec!
        var manualOwnerLabeledPayload = JsonSerializer.Serialize(new
        {
            action = "labeled",
            sender = new { login = "azureholic" },
            repository = new { full_name = "azureholic/contoso-smart-grid" },
            issue = new
            {
                number = 78,
                title = "Beatbox API fails to build locally",
                body = "Running dotnet run exits with npm error.",
                user = new { login = "azureholic" },
                labels = new[] { new { name = "bug" }, new { name = "factory" } }
            }
        });
        var manualResp = await SendSignedWebhookAsync("issues", Guid.NewGuid().ToString(), manualOwnerLabeledPayload);
        Assert.Equal(HttpStatusCode.Accepted, manualResp.StatusCode);

        var contosoRepoId = projects.EnumerateArray().First(p => p.GetProperty("slug").GetString() == "contoso-smart-grid").GetProperty("repoId").GetGuid();
        var contosoBoard = await _client.GetFromJsonAsync<JsonElement>($"/api/repos/{contosoRepoId}/board");
        Assert.DoesNotContain(contosoBoard.GetProperty("cards").EnumerateArray(), c => c.GetProperty("issueNumber").GetInt32() == 78);

        var inboxWith78 = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{contosoProjId}/inbox");
        Assert.Contains(inboxWith78.EnumerateArray(), i => i.GetProperty("issueNumber").GetInt32() == 78 && i.GetProperty("ownerAttemptedDirectWorkLabel").GetBoolean());

        // Fork PR from untrusted user -> ignored, gets no preview and no orchestration (§18 AC 17)
        var forkPrPayload = JsonSerializer.Serialize(new
        {
            action = "opened",
            sender = new { login = "fork-attacker" },
            repository = new { full_name = "azureholic/contoso-smart-grid" },
            pull_request = new
            {
                number = 999,
                user = new { login = "fork-attacker" },
                head = new
                {
                    sha = "badcafe123456",
                    repo = new { fork = true, full_name = "fork-attacker/contoso-smart-grid" }
                }
            }
        });
        var forkResp = await SendSignedWebhookAsync("pull_request", Guid.NewGuid().ToString(), forkPrPayload);
        Assert.Equal(HttpStatusCode.Accepted, forkResp.StatusCode);

        var audits = await _client.GetFromJsonAsync<JsonElement>("/api/audit?search=UntrustedPrIgnored");
        Assert.NotEmpty(audits.EnumerateArray());
    }

    [Fact]
    public async Task EndToEnd_StopTheLine_AutoReverts_Single_Suspect_And_Resumes_When_Main_Turns_Green()
    {
        var repos = await _client.GetFromJsonAsync<JsonElement>("/api/repos");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "contoso-smart-grid").GetProperty("id").GetGuid();

        // Simulate red main via signed workflow_run failure webhook (§6.2, §18 AC 14)
        var failWf = JsonSerializer.Serialize(new
        {
            action = "completed",
            sender = new { login = "azureholic" },
            repository = new { full_name = "azureholic/contoso-smart-grid" },
            workflow_run = new
            {
                id = 555001,
                status = "completed",
                conclusion = "failure",
                head_branch = "main"
            }
        });
        await SendSignedWebhookAsync("workflow_run", Guid.NewGuid().ToString(), failWf);

        var lineStopped = await _client.GetFromJsonAsync<JsonElement>($"/api/repos/{repoId}/line");
        Assert.Equal("LineStopped", lineStopped.GetProperty("state").GetString());
        Assert.Contains("revert PR", lineStopped.GetProperty("activeAction").GetString()!, StringComparison.OrdinalIgnoreCase);

        // Green main workflow_run automatically clears LineStopped
        var passWf = JsonSerializer.Serialize(new
        {
            action = "completed",
            sender = new { login = "azureholic" },
            repository = new { full_name = "azureholic/contoso-smart-grid" },
            workflow_run = new
            {
                id = 555002,
                status = "completed",
                conclusion = "success",
                head_branch = "main"
            }
        });
        await SendSignedWebhookAsync("workflow_run", Guid.NewGuid().ToString(), passWf);

        var lineRestored = await _client.GetFromJsonAsync<JsonElement>($"/api/repos/{repoId}/line");
        Assert.Equal("Running", lineRestored.GetProperty("state").GetString());
    }

    [Fact]
    public async Task EndToEnd_Rbac_TokenRevocation_Approval_And_Csv_Audit_Export()
    {
        // Viewer role is forbidden from pausing the factory or approving work items (§13)
        using var viewerReq = new HttpRequestMessage(HttpMethod.Post, "/api/factory:pause");
        viewerReq.Headers.Add("X-Test-Role", nameof(UserRole.Viewer));
        var viewerResp = await _client.SendAsync(viewerReq);
        Assert.Equal(HttpStatusCode.Forbidden, viewerResp.StatusCode);

        // Operator approves seeded AwaitingApproval work item #5 (PR #105)
        var wi5Id = "44444444-4444-4444-4444-444444444405";
        using var opApprove = new HttpRequestMessage(HttpMethod.Post, $"/api/workitems/{wi5Id}:approve")
        {
            Content = JsonContent.Create(new { reason = "Verified area:auth endpoints" })
        };
        opApprove.Headers.Add("X-Test-Role", nameof(UserRole.Operator));
        var opResp = await _client.SendAsync(opApprove);
        Assert.Equal(HttpStatusCode.OK, opResp.StatusCode);

        var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/workitems/{wi5Id}");
        Assert.Equal("AwaitingMerge", detail.GetProperty("workItem").GetProperty("state").GetString());

        // CSV Audit Export works
        var csvResp = await _client.GetAsync("/api/audit/export.csv");
        Assert.Equal(HttpStatusCode.OK, csvResp.StatusCode);
        var csvText = await csvResp.Content.ReadAsStringAsync();
        Assert.Contains("Id,Timestamp,Actor,Action", csvText);
        Assert.Contains("WorkItemApproved", csvText);

        // GitHub App authorization revoked webhook pauses all projects & sets reconnectRequired (§10.1, §18 AC 12)
        var revokePayload = JsonSerializer.Serialize(new
        {
            action = "revoked",
            sender = new { login = "azureholic" }
        });
        await SendSignedWebhookAsync("github_app_authorization", Guid.NewGuid().ToString(), revokePayload);

        var status = await _client.GetFromJsonAsync<JsonElement>("/api/factory/status");
        Assert.Equal("Paused", status.GetProperty("status").GetString());
        Assert.True(status.GetProperty("reconnectRequired").GetBoolean());
    }

    [Fact]
    public async Task EndToEnd_Login_Session_Cookie_GitHub_Redirect_And_Logout()
    {
        var isolatedDb = Path.Combine(Path.GetTempPath(), $"demofactory-auth-e2e-{Guid.NewGuid():N}.db");
        var isolatedDir = Path.Combine(Path.GetTempPath(), $"demofactory-auth-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolatedDir);

        try
        {
            using var browserFactory = new WebApplicationFactory<ApiAssemblyMarker>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Sqlite"] = $"Data Source={isolatedDb}",
                        ["Factory:SandboxMode"] = "true",
                        ["Factory:AllowTestHeaderAuth"] = "false",
                        ["Factory:LocalDataDirectory"] = isolatedDir,
                        ["Factory:PoliciesDirectory"] = isolatedDir,
                        ["Foundry:ProjectEndpoint"] = ""
                    });
                });
            });

            using var browserClient = browserFactory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true
            });

            // 1. Unauthenticated /auth/me returns 401; /auth/github/manifest-start emits a valid GitHub App manifest
            var unauthMe = await browserClient.GetAsync("/auth/me");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthMe.StatusCode);

            var manifestHtmlResp = await browserClient.GetAsync("/auth/github/manifest-start");
            Assert.Equal(HttpStatusCode.OK, manifestHtmlResp.StatusCode);
            var manifestJson = AuthEndpoints.BuildGitHubAppManifestJson("http://localhost:5173");
            using var manifestDoc = JsonDocument.Parse(manifestJson);
            var mRoot = manifestDoc.RootElement;
            Assert.Equal("https://example.com/webhooks/github", mRoot.GetProperty("hook_attributes").GetProperty("url").GetString());
            Assert.False(mRoot.GetProperty("hook_attributes").GetProperty("active").GetBoolean());
            Assert.Equal("read", mRoot.GetProperty("default_permissions").GetProperty("vulnerability_alerts").GetString());
            Assert.False(mRoot.GetProperty("default_permissions").TryGetProperty("dependabot_alerts", out _));

            // 2. Sign in via cookie session (/auth/sandbox/login) -> /auth/me returns 200
            var loginResp = await browserClient.PostAsJsonAsync("/auth/sandbox/login", new { login = "remcobrosky", role = "Admin" });
            Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);

            var authedMe = await browserClient.GetFromJsonAsync<JsonElement>("/auth/me");
            Assert.Equal("remcobrosky", authedMe.GetProperty("login").GetString());
            Assert.Equal("Admin", authedMe.GetProperty("role").GetString());

            // 3. Configuring GitHub OAuth credentials enables real 302 redirect to github.com/login/oauth/authorize
            var saveCfg = await browserClient.PostAsJsonAsync("/auth/config/github", new
            {
                clientId = "Iv23liRealGitHubAppTest",
                clientSecret = "secret_test_value_12345"
            });
            Assert.Equal(HttpStatusCode.OK, saveCfg.StatusCode);

            var ghLoginRedirect = await browserClient.GetAsync("/auth/github/login");
            Assert.Equal(HttpStatusCode.Redirect, ghLoginRedirect.StatusCode);
            Assert.StartsWith("https://github.com/login/oauth/authorize?client_id=Iv23liRealGitHubAppTest", ghLoginRedirect.Headers.Location?.ToString());

            // 4. Logout clears df_session cookie -> /auth/me returns 401 again
            var logoutResp = await browserClient.PostAsync("/auth/logout", null);
            Assert.Equal(HttpStatusCode.OK, logoutResp.StatusCode);

            var afterLogoutMe = await browserClient.GetAsync("/auth/me");
            Assert.Equal(HttpStatusCode.Unauthorized, afterLogoutMe.StatusCode);
        }
        finally
        {
            if (File.Exists(isolatedDb)) { try { File.Delete(isolatedDb); } catch { } }
            if (Directory.Exists(isolatedDir)) { try { Directory.Delete(isolatedDir, recursive: true); } catch { } }
        }
    }

    [Fact]
    public async Task EndToEnd_SystemReadiness_ExecutionMode_And_LiveGitHubGatewayFallback()
    {
        var isolatedDb = Path.Combine(Path.GetTempPath(), $"demofactory-live-e2e-{Guid.NewGuid():N}.db");
        var isolatedDir = Path.Combine(Path.GetTempPath(), $"demofactory-live-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolatedDir);

        var recordedPaths = new List<string>();
        var rulesetPostAttempts = 0;

        try
        {
            using var liveFactory = new WebApplicationFactory<ApiAssemblyMarker>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Sqlite"] = $"Data Source={isolatedDb}",
                        ["Factory:SandboxMode"] = "true",
                        ["Factory:AllowTestHeaderAuth"] = "true",
                        ["Factory:LocalDataDirectory"] = isolatedDir,
                        ["Factory:PoliciesDirectory"] = isolatedDir,
                        ["Foundry:ProjectEndpoint"] = "",
                        ["GitHub:AppId"] = "5758548",
                        ["GitHub:AppSlug"] = "azureholic-demo-factory",
                        ["GitHub:ClientId"] = "Iv23liUfMKApH3rhp0J4",
                        ["GitHub:ClientSecret"] = "test-client-secret",
                        ["GitHub:WritePaceMilliseconds"] = "1"
                    });
                });

                b.ConfigureServices(services =>
                {
                    services.AddHttpClient("GitHubApi")
                        .ConfigurePrimaryHttpMessageHandler(() => new StubGitHubHttpHandler((req) =>
                        {
                            var path = $"{req.Method} {req.RequestUri!.AbsolutePath}";
                            lock (recordedPaths) recordedPaths.Add(path);

                            // Simulate missing template repo -> triggers direct POST /user/repos fallback
                            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath.Contains("demo-factory-template", StringComparison.OrdinalIgnoreCase))
                                return StubJson(HttpStatusCode.NotFound, new { message = "Not Found" });

                            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath == "/repos/azureholic/live-test-repo")
                            {
                                var createdAlready = recordedPaths.Any(p => p == "POST /user/repos");
                                return createdAlready
                                    ? StubJson(HttpStatusCode.OK, new { name = "live-test-repo", default_branch = "main", allow_auto_merge = true, allow_squash_merge = true })
                                    : StubJson(HttpStatusCode.NotFound, new { message = "Not Found" });
                            }

                            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath == "/user")
                                return StubJson(HttpStatusCode.OK, new { login = "azureholic" });

                            if (req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath == "/user/repos")
                                return StubJson(HttpStatusCode.Created, new { name = "live-test-repo", default_branch = "main" });

                            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath.Contains("/git/ref/heads/", StringComparison.Ordinal))
                                return StubJson(HttpStatusCode.OK, new { @object = new { sha = "abc123def456" } });

                            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath.Contains("/contents/", StringComparison.Ordinal))
                                return StubJson(HttpStatusCode.NotFound, new { message = "Not Found" });

                            if (req.Method == HttpMethod.Put && req.RequestUri.AbsolutePath.Contains("/contents/.github/workflows/", StringComparison.OrdinalIgnoreCase))
                                return StubJson(HttpStatusCode.Forbidden, new { message = "Resource not accessible by integration" });

                            if (req.Method == HttpMethod.Put && req.RequestUri.AbsolutePath.Contains("/contents/", StringComparison.Ordinal))
                                return StubJson(HttpStatusCode.Created, new { content = new { sha = "file_sha_1" } });

                            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath.EndsWith("/rulesets", StringComparison.Ordinal))
                                return StubJson(HttpStatusCode.OK, Array.Empty<object>());

                            // First ruleset POST returns 422 (simulating plan restriction on copilot_code_review/code_scanning) -> second succeeds!
                            if (req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath.EndsWith("/rulesets", StringComparison.Ordinal))
                            {
                                return Interlocked.Increment(ref rulesetPostAttempts) == 1
                                    ? StubJson(HttpStatusCode.UnprocessableEntity, new { message = "Rule not supported on plan" })
                                    : StubJson(HttpStatusCode.Created, new { id = 101, name = "demo-factory-main-protection" });
                            }

                            if (req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath == "/graphql")
                            {
                                var bodyText = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                                if (bodyText.Contains("ResolveRepoAndOwner", StringComparison.Ordinal))
                                    return StubJson(HttpStatusCode.OK, new { data = new { repository = new { id = "R_kgDO1", owner = new { id = "U_kgDO1" } } } });
                                if (bodyText.Contains("createProjectV2", StringComparison.Ordinal))
                                    return StubJson(HttpStatusCode.OK, new { data = new { createProjectV2 = new { projectV2 = new { id = "PVT_live_99", number = 7 } } } });
                                if (bodyText.Contains("linkProjectV2ToRepository", StringComparison.Ordinal))
                                    return StubJson(HttpStatusCode.OK, new { data = new { linkProjectV2ToRepository = new { repository = new { id = "R_kgDO1" } } } });
                                if (bodyText.Contains("addProjectV2ItemById", StringComparison.Ordinal))
                                    return StubJson(HttpStatusCode.OK, new { data = new { addProjectV2ItemById = new { item = new { id = "PVTI_live_1" } } } });
                                return StubJson(HttpStatusCode.OK, new { data = new { } });
                            }

                            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath.Contains("/labels/", StringComparison.Ordinal))
                                return StubJson(HttpStatusCode.NotFound, new { message = "Not Found" });

                            if (req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath.EndsWith("/issues", StringComparison.Ordinal))
                                return StubJson(HttpStatusCode.Created, new { number = 1, node_id = "I_live_1", html_url = "https://github.com/azureholic/live-test-repo/issues/1" });

                            return StubJson(HttpStatusCode.OK, new { ok = true });
                        }));
                });
            });

            using var client = liveFactory.CreateClient();

            // 1. /api/system/readiness reports Sandbox mode + seeded Sandbox token -> switching to Live without Force is rejected
            var readiness = await client.GetFromJsonAsync<JsonElement>("/api/system/readiness");
            Assert.True(readiness.GetProperty("sandboxMode").GetBoolean());
            Assert.Equal("azureholic-demo-factory", readiness.GetProperty("gitHub").GetProperty("appSlug").GetString());
            Assert.Equal("Sandbox", readiness.GetProperty("gitHub").GetProperty("tokenKind").GetString());

            var blockedSwitch = await client.PostAsJsonAsync("/api/system/mode", new { sandboxMode = false, force = false });
            Assert.Equal(HttpStatusCode.BadRequest, blockedSwitch.StatusCode);

            // 2. Store a real GitHub user token via ITokenBroker, then switch to Live GitHub mode
            var broker = liveFactory.Services.GetRequiredService<ITokenBroker>();
            await broker.StoreRotatedTokensAsync("azureholic", "ghu_real_live_token", TimeSpan.FromHours(8), "ghr_real_live_refresh", TimeSpan.FromDays(90));

            var liveSwitch = await client.PostAsJsonAsync("/api/system/mode", new { sandboxMode = false });
            Assert.Equal(HttpStatusCode.OK, liveSwitch.StatusCode);
            Assert.True(File.Exists(Path.Combine(isolatedDir, "runtime-mode.json")));

            var statusAfter = await client.GetFromJsonAsync<JsonElement>("/api/factory/status");
            Assert.False(statusAfter.GetProperty("sandboxMode").GetBoolean());

            // 3. Exercise live GitHubGateway fallback repo creation, ruleset fallback, createProjectV2, and issue creation
            var gateway = liveFactory.Services.GetRequiredService<IGitHubGateway>();
            var dbFactory = liveFactory.Services.GetRequiredService<IDbContextFactory<DemoFactoryDbContext>>();
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var p = new ProjectEntity { Name = "Live Test", Slug = "live-test-repo", SpecMarkdown = "# Spec" };
                db.Projects.Add(p);
                db.Repositories.Add(new RepositoryEntity { ProjectId = p.Id, Owner = "azureholic", Name = "live-test-repo", InstallationId = 0 });
                await db.SaveChangesAsync();
            }

            var repoUrl = await gateway.CreateRepositoryFromTemplateAsync(
                "azureholic", "demo-factory-template-dotnet-react", "azureholic", "live-test-repo", true, "# Spec", "# ADR");
            Assert.Equal("https://github.com/azureholic/live-test-repo", repoUrl);
            Assert.Contains("POST /user/repos", recordedPaths);
            Assert.Contains("PUT /repos/azureholic/live-test-repo/contents/docs/spec.md", recordedPaths);

            await gateway.ApplyRulesetsAndSettingsAsync("azureholic", "live-test-repo", ["build", "test", "lint"]);
            Assert.Equal(2, rulesetPostAttempts);

            var (projId, projNum) = await gateway.CopyProjectBoardAsync("azureholic", "live-test-repo", "Live Test Board");
            Assert.Equal("PVT_live_99", projId);
            Assert.Equal(7, projNum);

            var issue = await gateway.CreateIssueAsync("azureholic", "live-test-repo", "Walking skeleton", "Body", ["factory"]);
            Assert.Equal(1, issue.Number);
            Assert.Contains("POST /repos/azureholic/live-test-repo/labels", recordedPaths);

            await gateway.UnlockConversationAsync("azureholic", "live-test-repo", 1);
            await gateway.UpdateIssueAsync("azureholic", "live-test-repo", 1, "Walking skeleton (clarified)", "Updated body");
            Assert.Contains("DELETE /repos/azureholic/live-test-repo/issues/1/lock", recordedPaths);
            Assert.Contains("PATCH /repos/azureholic/live-test-repo/issues/1", recordedPaths);
        }
        finally
        {
            if (File.Exists(isolatedDb)) { try { File.Delete(isolatedDb); } catch { } }
            if (Directory.Exists(isolatedDir)) { try { Directory.Delete(isolatedDir, recursive: true); } catch { } }
        }
    }

    [Fact]
    public async Task EndToEnd_WorkItemClarify_And_SpecEdit_UpdatesBodyAndDispatchesToCopilot()
    {
        var createResp = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = "Clarification Flow Demo",
            specMarkdown = "# Clarification Flow Demo\n\n## Goals\n- Interactive browser beatbox sequencer.",
            templateId = "dotnet-react",
            autonomy = "L1",
            codingModel = "gemini-4-carbon"
        });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = created.GetProperty("project").GetProperty("id").GetGuid();

        await _client.PostAsJsonAsync($"/api/projects/{projectId}:clarify", new { answers = new Dictionary<string, string>(), dismissPublicSafeFlags = true });
        var approveResp = await _client.PostAsync($"/api/projects/{projectId}:approve", null);
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

        var repos = await _client.GetFromJsonAsync<JsonElement>("/api/repos");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("projectId").GetGuid() == projectId).GetProperty("id").GetGuid();

        // Force an unstarted work item into NeedsClarification with an empty body (which would otherwise fail ReviewSpecAsync again)
        Guid targetWorkItemId;
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<DemoFactoryDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var wi = await db.WorkItems.OrderBy(w => w.IssueNumber).FirstAsync(w => w.RepoId == repoId);
            targetWorkItemId = wi.Id;
            wi.State = WorkItemStateKind.NeedsClarification;
            wi.BoardStatus = BoardStatus.Blocked;
            wi.PmVerdict = "NeedsClarification";
            wi.BlockedReason = "Should /healthz return JSON?";
            wi.BodyMarkdown = "";
            wi.AcceptanceCriteriaJson = "[]";
            wi.BlockedByIssueNumbersJson = "[]";
            await db.SaveChangesAsync();
        }

        // 1. Update specification & acceptance criteria in-app via PUT /api/workitems/{id}/spec
        var putSpecResp = await _client.PutAsJsonAsync($"/api/workitems/{targetWorkItemId}/spec", new
        {
            title = "Walking skeleton: Scaffold dotnet-react & health probe",
            bodyMarkdown = "Scaffold the .NET 11 + React 19 walking skeleton.",
            acceptanceCriteria = new[] { "GET /healthz returns 200 OK JSON", "Frontend renders sequencer grid" }
        });
        Assert.Equal(HttpStatusCode.OK, putSpecResp.StatusCode);

        // 2. Submit operator clarification via POST /api/workitems/{id}:clarify -> dispatches directly to Implementing!
        var clarifyResp = await _client.PostAsJsonAsync($"/api/workitems/{targetWorkItemId}:clarify", new
        {
            clarificationMarkdown = "Return {\"status\":\"healthy\"} on GET /healthz and keep seed data in memory.",
            proceedWithDefaults = false
        });
        Assert.Equal(HttpStatusCode.OK, clarifyResp.StatusCode);

        // 3. Verify GET /api/workitems/{id} shows updated spec, issueUrl, cleared block, and Implementing state
        var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/workitems/{targetWorkItemId}");
        var wiDto = detail.GetProperty("workItem");
        Assert.Equal("Implementing", wiDto.GetProperty("state").GetString());
        Assert.Equal("Ready", wiDto.GetProperty("pmVerdict").GetString());
        Assert.Contains("github.com/", wiDto.GetProperty("issueUrl").GetString());
        Assert.Contains("Operator Clarification", wiDto.GetProperty("bodyMarkdown").GetString());
        Assert.Equal(2, wiDto.GetProperty("acceptanceCriteria").GetArrayLength());
    }

    [Fact]
    public async Task EndToEnd_LiveReconciler_DetectsCopilotWorkFinished_MarksReady_EvaluatesGate_And_SquashMerges()
    {
        var isolatedDb = Path.Combine(Path.GetTempPath(), $"demofactory-reconcile-{Guid.NewGuid():N}.db");
        var isolatedDir = Path.Combine(Path.GetTempPath(), $"demofactory-reconcile-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolatedDir);

        var isDraft = true;
        var isMerged = false;
        var markedReadyViaGraphQl = false;
        var squashMergedViaRest = false;

        try
        {
            using var reconcileFactory = new WebApplicationFactory<ApiAssemblyMarker>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Sqlite"] = $"Data Source={isolatedDb}",
                        ["Factory:SandboxMode"] = "false",
                        ["Factory:AllowTestHeaderAuth"] = "true",
                        ["Factory:LocalDataDirectory"] = isolatedDir,
                        ["Factory:PoliciesDirectory"] = isolatedDir,
                        ["Foundry:ProjectEndpoint"] = "",
                        ["GitHub:AppId"] = "5758548",
                        ["GitHub:AppSlug"] = "azureholic-demo-factory",
                        ["GitHub:ClientId"] = "Iv23liUfMKApH3rhp0J4",
                        ["GitHub:ClientSecret"] = "test-client-secret",
                        ["GitHub:WritePaceMilliseconds"] = "1"
                    });
                });

                b.ConfigureServices(services =>
                {
                    // Remove periodic hosted worker in test so we drive ticks deterministically
                    var hosted = services.FirstOrDefault(d => d.ImplementationType == typeof(BoardPollerAndSweeperWorker));
                    if (hosted is not null) services.Remove(hosted);

                    services.AddHttpClient("GitHubApi")
                        .ConfigurePrimaryHttpMessageHandler(() => new StubGitHubHttpHandler(req =>
                        {
                            var path = req.RequestUri!.AbsolutePath;

                            if (req.Method == HttpMethod.Get && path.EndsWith("/contents/.github/workflows/ci.yml", StringComparison.Ordinal))
                                return StubJson(HttpStatusCode.OK, new { sha = "ci_sha" });

                            if (req.Method == HttpMethod.Get && path.EndsWith("/rulesets", StringComparison.Ordinal))
                                return StubJson(HttpStatusCode.OK, Array.Empty<object>());

                            if (req.Method == HttpMethod.Get && path == "/repos/azureholic/browser-beatbox-628/pulls")
                            {
                                if (isMerged)
                                    return StubJson(HttpStatusCode.OK, Array.Empty<object>());

                                return StubJson(HttpStatusCode.OK, new[]
                                {
                                    new
                                    {
                                        number = 10,
                                        node_id = "PR_kwDO_10",
                                        title = "Scaffold dotnet-react walking skeleton (REQ-001)",
                                        body = "Implements walking skeleton.\n\n- Fixes #1",
                                        draft = isDraft,
                                        state = "open",
                                        merged = false,
                                        mergeable_state = "clean",
                                        user = new { login = "azureholic" },
                                        head = new { @ref = "copilot/scaffold-skeleton", sha = "719f68aba10c", repo = new { fork = false, full_name = "azureholic/browser-beatbox-628" } },
                                        @base = new { @ref = "main" }
                                    }
                                });
                            }

                            if (req.Method == HttpMethod.Get && path == "/repos/azureholic/browser-beatbox-628/pulls/10")
                            {
                                return StubJson(HttpStatusCode.OK, new
                                {
                                    number = 10,
                                    node_id = "PR_kwDO_10",
                                    title = "Scaffold dotnet-react walking skeleton (REQ-001)",
                                    body = "Implements walking skeleton.\n\n- Fixes #1",
                                    draft = isDraft,
                                    state = isMerged ? "closed" : "open",
                                    merged = isMerged,
                                    mergeable_state = "clean",
                                    requested_reviewers = Array.Empty<object>(),
                                    user = new { login = "azureholic" },
                                    head = new { @ref = "copilot/scaffold-skeleton", sha = "719f68aba10c", repo = new { fork = false, full_name = "azureholic/browser-beatbox-628" } },
                                    @base = new { @ref = "main" }
                                });
                            }

                            if (req.Method == HttpMethod.Get && path == "/repos/azureholic/browser-beatbox-628/issues/10/timeline")
                            {
                                return StubJson(HttpStatusCode.OK, new object[]
                                {
                                    new { @event = "copilot_work_started" },
                                    new { @event = "copilot_work_finished" }
                                });
                            }

                            if (req.Method == HttpMethod.Post && path == "/graphql")
                            {
                                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                                if (body.Contains("markPullRequestReadyForReview", StringComparison.Ordinal))
                                {
                                    isDraft = false;
                                    markedReadyViaGraphQl = true;
                                    return StubJson(HttpStatusCode.OK, new { data = new { markPullRequestReadyForReview = new { pullRequest = new { isDraft = false } } } });
                                }
                                return StubJson(HttpStatusCode.OK, new { data = new { enablePullRequestAutoMerge = new { pullRequest = new { id = "PR_kwDO_10" } } } });
                            }

                            if (req.Method == HttpMethod.Get && path.Contains("/commits/719f68aba10c/check-runs", StringComparison.Ordinal))
                            {
                                return StubJson(HttpStatusCode.OK, new
                                {
                                    total_count = 3,
                                    check_runs = new[]
                                    {
                                        new { name = "build", status = "completed", conclusion = "success", app = new { slug = "github-actions" } },
                                        new { name = "test", status = "completed", conclusion = "success", app = new { slug = "github-actions" } },
                                        new { name = "lint", status = "completed", conclusion = "success", app = new { slug = "github-actions" } }
                                    }
                                });
                            }

                            if (req.Method == HttpMethod.Get && path == "/repos/azureholic/browser-beatbox-628/pulls/10/reviews")
                            {
                                return StubJson(HttpStatusCode.OK, new[]
                                {
                                    new { id = 1, commit_id = "719f68aba10c", state = "COMMENTED", user = new { login = "copilot-pull-request-reviewer[bot]" } }
                                });
                            }

                            if (req.Method == HttpMethod.Get && path == "/repos/azureholic/browser-beatbox-628/pulls/10/comments")
                                return StubJson(HttpStatusCode.OK, Array.Empty<object>());

                            if (req.Method == HttpMethod.Get && path == "/repos/azureholic/browser-beatbox-628/pulls/10/files")
                            {
                                return StubJson(HttpStatusCode.OK, new[]
                                {
                                    new { filename = "src/Beatbox.Api/Program.cs", additions = 45, deletions = 0, status = "added" },
                                    new { filename = "web/package-lock.json", additions = 3200, deletions = 0, status = "added" }
                                });
                            }

                            if (req.Method == HttpMethod.Put && path == "/repos/azureholic/browser-beatbox-628/pulls/10/merge")
                            {
                                isMerged = true;
                                squashMergedViaRest = true;
                                return StubJson(HttpStatusCode.OK, new { sha = "merged_sha_999", merged = true });
                            }

                            return StubJson(HttpStatusCode.OK, new { ok = true });
                        }));
                });
            });

            using var _ = reconcileFactory.CreateClient();
            var broker = reconcileFactory.Services.GetRequiredService<ITokenBroker>();
            await broker.StoreRotatedTokensAsync("azureholic", "ghu_live_token_reconcile", TimeSpan.FromHours(8), "ghr_live_refresh", TimeSpan.FromDays(90));

            var dbFactory = reconcileFactory.Services.GetRequiredService<IDbContextFactory<DemoFactoryDbContext>>();
            Guid repoId;
            Guid wi1Id;
            Guid wi2Id;
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var proj = new ProjectEntity { Name = "Browser Beatbox", Slug = "browser-beatbox-628", SpecMarkdown = "# Beatbox", TemplateId = "dotnet-react", IsPublic = true };
                db.Projects.Add(proj);
                var repo = new RepositoryEntity { ProjectId = proj.Id, Owner = "azureholic", Name = "browser-beatbox-628", InstallationId = 0 };
                db.Repositories.Add(repo);
                db.LineStates.Add(new LineStateEntity { RepoId = repo.Id, State = LineStatus.Running });

                var wi1 = new WorkItemEntity
                {
                    RepoId = repo.Id,
                    IssueNumber = 1,
                    Title = "Walking skeleton",
                    BodyMarkdown = "Scaffold the .NET 11 + React walking skeleton and health probe.",
                    AcceptanceCriteriaJson = "[\"Healthz returns 200\"]",
                    LabelsJson = "[\"factory\"]",
                    IssueAuthor = "azureholic",
                    WorkLabelAppliedBy = "azureholic",
                    PassesWorkLabelRule = true,
                    GateProfile = GateProfile.Feature,
                    State = WorkItemStateKind.Implementing,
                    BoardStatus = BoardStatus.InProgress
                };

                // Downstream item #2 starts in Backlog blocked by #1 -> orchestrator must auto-promote & dispatch once #1 merges!
                var wi2 = new WorkItemEntity
                {
                    RepoId = repo.Id,
                    IssueNumber = 2,
                    Title = "Acceptance Test: 16-step continuous looping sequencer",
                    BodyMarkdown = "Add executable acceptance tests verifying 16-step loop progression.",
                    AcceptanceCriteriaJson = "[\"Sequencer advances through all 16 steps\"]",
                    BlockedByIssueNumbersJson = "[1]",
                    LabelsJson = "[\"factory\",\"acceptance-test\"]",
                    IssueAuthor = "azureholic",
                    WorkLabelAppliedBy = "azureholic",
                    PassesWorkLabelRule = true,
                    IsAcceptanceTestIssue = true,
                    GateProfile = GateProfile.AcceptanceTest,
                    State = WorkItemStateKind.Queued,
                    BoardStatus = BoardStatus.Backlog
                };

                db.WorkItems.AddRange(wi1, wi2);
                await db.SaveChangesAsync();
                repoId = repo.Id;
                wi1Id = wi1.Id;
                wi2Id = wi2.Id;
            }

            var coordinator = reconcileFactory.Services.GetRequiredService<IFactoryOrchestrationCoordinator>();

            // Tick 1: Detects copilot_work_finished on Draft PR #10 -> marks Ready for Review via GraphQL & transitions #1 to Validating (#2 stays in Backlog while #1 is open)
            var p1 = await coordinator.ReconcileActiveWorkItemsOnceAsync(repoId);
            Assert.True(p1 > 0);
            Assert.True(markedReadyViaGraphQl);

            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var wi = await db.WorkItems.FirstAsync(w => w.Id == wi1Id);
                var wi2 = await db.WorkItems.FirstAsync(w => w.Id == wi2Id);
                Assert.Equal(WorkItemStateKind.Validating, wi.State);
                Assert.Equal(10, wi.PrNumber);
                Assert.Equal(BoardStatus.Backlog, wi2.BoardStatus);
            }

            // Tick 2: Evaluates green CI + Copilot review -> squash-merges PR #10 -> marks #1 Merged/Done AND automatically promotes & dispatches #2 from Backlog -> InProgress/Implementing!
            var p2 = await coordinator.ReconcileActiveWorkItemsOnceAsync(repoId);
            Assert.True(p2 > 0);
            Assert.True(squashMergedViaRest);

            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var wi1 = await db.WorkItems.FirstAsync(w => w.Id == wi1Id);
                var wi2 = await db.WorkItems.FirstAsync(w => w.Id == wi2Id);
                Assert.Equal(WorkItemStateKind.Merged, wi1.State);
                Assert.Equal(BoardStatus.Done, wi1.BoardStatus);
                Assert.Equal(WorkItemStateKind.Implementing, wi2.State);
                Assert.Equal(BoardStatus.InProgress, wi2.BoardStatus);
            }

            // Verify /api/metrics reports strictly real SQL counts (0 on inactive prior days, never synthetic modulo values)
            using var metricsClient = reconcileFactory.CreateClient();
            var metrics = await metricsClient.GetFromJsonAsync<JsonElement>("/api/metrics");
            Assert.Equal(1, metrics.GetProperty("totalMergedCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, metrics.GetProperty("mttrMinutes").ValueKind);

            var trend = metrics.GetProperty("trend30Days").EnumerateArray().ToList();
            Assert.Equal(14, trend.Count);
            for (var i = 0; i < 13; i++)
            {
                Assert.Equal(0, trend[i].GetProperty("merged").GetInt32());
                Assert.Equal(0, trend[i].GetProperty("sessions").GetInt32());
                Assert.Equal(JsonValueKind.Null, trend[i].GetProperty("avgRounds").ValueKind);
            }
            Assert.Equal(1, trend[13].GetProperty("merged").GetInt32());
            Assert.Equal(1.0, trend[13].GetProperty("avgRounds").GetDouble());
        }
        finally
        {
            if (File.Exists(isolatedDb)) { try { File.Delete(isolatedDb); } catch { } }
            if (Directory.Exists(isolatedDir)) { try { Directory.Delete(isolatedDir, recursive: true); } catch { } }
        }
    }

    private static HttpResponseMessage StubJson(HttpStatusCode code, object payload) =>
        new(code)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

    private sealed class StubGitHubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private Task<HttpResponseMessage> SendSignedWebhookAsync(string eventType, string deliveryId, string jsonBody)
    {
        var bytes = Encoding.UTF8.GetBytes(jsonBody);
        var sig = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), bytes)).ToLowerInvariant();

        var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/github")
        {
            Content = new ByteArrayContent(bytes)
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.Add("X-Hub-Signature-256", $"sha256={sig}");
        req.Headers.Add("X-GitHub-Delivery", deliveryId);
        req.Headers.Add("X-GitHub-Event", eventType);
        req.Headers.Add("X-DemoFactory-Sync-Process", "true");
        return _client.SendAsync(req);
    }
}
