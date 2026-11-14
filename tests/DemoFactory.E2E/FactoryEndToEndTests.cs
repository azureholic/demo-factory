using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DemoFactory.Api;
using DemoFactory.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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
                    ["Factory:SeedDemoData"] = "true",
                    ["Factory:OwnerLogin"] = "azureholic",
                    ["Factory:PoliciesDirectory"] = _policiesDir,
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
        Assert.Contains(inbox.EnumerateArray(), i => i.GetProperty("issueNumber").GetInt32() == 77);

        // Turn inbox issue #77 into a vetted feature draft (§5.6, AC 27)
        var fromIssuesResp = await _client.PostAsJsonAsync($"/api/projects/{contosoProjId}/features:from-issues", new
        {
            issueNumbers = new[] { 77 }
        });
        Assert.Equal(HttpStatusCode.Created, fromIssuesResp.StatusCode);

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
