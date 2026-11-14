using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;
using DemoFactory.Domain;
using Json.Schema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DemoFactory.Agents;

public sealed class FoundryAgentOptions
{
    public string ProjectEndpoint { get; set; } = string.Empty;
    public string DefaultDeployment { get; set; } = "orchestrator-model";
    public string Region { get; set; } = "swedencentral";
    public string PromptsDirectory { get; set; } = "agents/pm";
    public string PromptVersion { get; set; } = "v1.0";
    public string ManagedIdentityClientId { get; set; } = string.Empty;
}

public sealed record PmExecutionTrace(
    string Operation,
    string PromptVersion,
    string ModelDeployment,
    bool UsedDeterministicFallback,
    int Attempts,
    string OutputJson,
    DateTimeOffset Timestamp);

public interface IPmTraceSink
{
    Task RecordTraceAsync(PmExecutionTrace trace, CancellationToken ct = default);
}

public sealed class NullPmTraceSink : IPmTraceSink
{
    public Task RecordTraceAsync(PmExecutionTrace trace, CancellationToken ct = default) => Task.CompletedTask;
}

public interface IProjectManagerModelTransport
{
    Task<string> InvokeJsonAsync(string systemInstructions, string userPrompt, string deploymentName, CancellationToken ct = default);
}

/// <summary>
/// Microsoft Agent Framework + Microsoft Foundry transport (§3.2, §9.1):
/// Creates an AIAgent from AIProjectClient(...).AsAIAgent(model, instructions, name) authenticated via Managed Identity.
/// </summary>
public sealed class FoundryAgentFrameworkTransport(IOptions<FoundryAgentOptions> options) : IProjectManagerModelTransport
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(3) };

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.Value.ProjectEndpoint)
        && Uri.TryCreate(options.Value.ProjectEndpoint, UriKind.Absolute, out _);

    private TokenCredential CreateCredential() =>
        string.IsNullOrWhiteSpace(options.Value.ManagedIdentityClientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.Value.ManagedIdentityClientId));

    public AIAgent CreateFoundryAgent(string? deploymentOverride = null, string? instructionsOverride = null)
    {
        var endpoint = string.IsNullOrWhiteSpace(options.Value.ProjectEndpoint)
            ? "https://aif-demofactory-dev-swc.services.ai.azure.com/api/projects/proj-demofactory-dev-swc"
            : options.Value.ProjectEndpoint;

        var projectClient = new AIProjectClient(new Uri(endpoint), CreateCredential());
        var model = string.IsNullOrWhiteSpace(deploymentOverride) ? options.Value.DefaultDeployment : deploymentOverride;
        var instructions = instructionsOverride ?? LoadSystemInstructions(options.Value.PromptsDirectory);

        return projectClient.AsAIAgent(
            model: model,
            instructions: instructions,
            name: "ProjectManagerAgent");
    }

    public async Task<string> InvokeJsonAsync(
        string systemInstructions,
        string userPrompt,
        string deploymentName,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Foundry ProjectEndpoint is not configured; using deterministic PM fallback.");

        var normalizedModel = deploymentName.StartsWith("foundry/", StringComparison.OrdinalIgnoreCase)
            ? deploymentName["foundry/".Length..]
            : deploymentName;

        try
        {
            var agent = CreateFoundryAgent(normalizedModel, systemInstructions);
            var response = await agent.RunAsync(userPrompt, cancellationToken: ct);
            if (!string.IsNullOrWhiteSpace(response.Text))
                return response.Text;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Unified MaaS reasoning models (such as MAI-Thinking-1 / orchestrator-model with disable-maap=true)
            // serve inference over Microsoft Foundry's Unified Chat Completions endpoint.
        }

        return await InvokeFoundryUnifiedChatCompletionsAsync(systemInstructions, userPrompt, normalizedModel, ct);
    }

    private async Task<string> InvokeFoundryUnifiedChatCompletionsAsync(
        string systemInstructions,
        string userPrompt,
        string deploymentName,
        CancellationToken ct)
    {
        var projectUri = new Uri(options.Value.ProjectEndpoint);
        var baseAuthority = $"{projectUri.Scheme}://{projectUri.Host}";
        var token = await CreateCredential().GetTokenAsync(
            new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
            ct);

        var candidateUrls = new[]
        {
            $"{baseAuthority}/models/chat/completions?api-version=2024-05-01-preview",
            $"{baseAuthority}/openai/deployments/{Uri.EscapeDataString(deploymentName)}/chat/completions?api-version=2025-01-01-preview"
        };

        Exception? lastErr = null;
        foreach (var url in candidateUrls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(new
                    {
                        model = deploymentName,
                        messages = new object[]
                        {
                            new { role = "system", content = systemInstructions },
                            new { role = "user", content = userPrompt }
                        },
                        max_completion_tokens = 8192
                    })
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

                using var resp = await SharedHttp.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var bodyErr = await resp.Content.ReadAsStringAsync(ct);
                    lastErr = new HttpRequestException($"Foundry chat completion {url} returned HTTP {(int)resp.StatusCode}: {bodyErr}");
                    continue;
                }

                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var contentProp))
                {
                    return contentProp.GetString() ?? string.Empty;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastErr = ex;
            }
        }

        throw lastErr ?? new InvalidOperationException($"Unable to invoke Foundry deployment '{deploymentName}'.");
    }

    public static string LoadSystemInstructions(string promptsDir)
    {
        var candidate = Path.Combine(promptsDir, "system.md");
        if (File.Exists(candidate))
            return File.ReadAllText(candidate);

        var rootCandidate = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", promptsDir, "system.md"));
        if (File.Exists(rootCandidate))
            return File.ReadAllText(rootCandidate);

        return "You are the Demo-Factory Project Manager Agent. Return valid JSON only.";
    }
}

public interface IProjectManagerAgent
{
    PublicSafeCheckOutput CheckPublicSafeDeterministic(string markdown);
    Task<SpecClarificationOutput> AnalyzeSpecForIntakeAsync(string specMarkdown, string? modelDeployment = null, CancellationToken ct = default);
    Task<GeneratedPlanOutput> GeneratePlanAsync(string projectName, string clarifiedSpec, string templateId, string? modelDeployment = null, CancellationToken ct = default);
    Task<PlanIncrementOutput> PlanIncrementAsync(string featureTitle, string featureSpec, ProjectIncrementContext context, string? modelDeployment = null, CancellationToken ct = default);
    Task<ChangeProposalOutput> ProposeChangesAsync(string oldSpec, string newSpec, IReadOnlyList<BacklogSummaryItem> backlog, string? modelDeployment = null, CancellationToken ct = default);
    Task<InboxGroupingOutput> GroupInboxAsync(IReadOnlyList<UntrustedInboxIssue> issues, string? modelDeployment = null, CancellationToken ct = default);
    Task<DraftedFeatureOutput> DraftFeatureFromIssuesAsync(IReadOnlyList<UntrustedInboxIssue> issues, string? modelDeployment = null, CancellationToken ct = default);
    Task<SpecReviewOutput> ReviewSpecAsync(IssueSpecInput issue, string? modelDeployment = null, CancellationToken ct = default);
    Task<TouchSetEstimateOutput> EstimateTouchSetAsync(IssueSpecInput issue, IReadOnlyList<string> repoTreePaths, string? modelDeployment = null, CancellationToken ct = default);
    Task<ComposedInstructionsOutput> ComposeInstructionsAsync(IssueSpecInput issue, string? acceptanceContractMarkdown, string? modelDeployment = null, CancellationToken ct = default);
    Task<FindingTriageOutput> TriageFindingsAsync(IReadOnlyList<TrackedFinding> findings, string diffSummary, string? modelDeployment = null, CancellationToken ct = default);
    Task<SpecConformanceOutput> CheckConformanceAsync(IssueSpecInput issue, string diffSummary, string? modelDeployment = null, CancellationToken ct = default);
    Task<DecompositionSuggestionOutput> SuggestDecompositionAsync(IssueSpecInput issue, string? modelDeployment = null, CancellationToken ct = default);
    Task<ReorderOutput> SuggestReorderAsync(IReadOnlyList<BacklogSummaryItem> readySet, string? modelDeployment = null, CancellationToken ct = default);
}

/// <summary>
/// Project Manager Agent (§3.2, §9.1):
/// Validates every structured JSON output against a JsonSchema, retries ONCE on schema/parse failure,
/// and falls back to deterministic logic when unavailable or invalid after retry.
/// </summary>
public sealed partial class ProjectManagerAgent(
    IProjectManagerModelTransport transport,
    IPmTraceSink traceSink,
    IOptions<FoundryAgentOptions> options,
    ILogger<ProjectManagerAgent> logger) : IProjectManagerAgent
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public PublicSafeCheckOutput CheckPublicSafeDeterministic(string markdown)
    {
        var flags = new List<PublicSafeFlag>();
        if (string.IsNullOrWhiteSpace(markdown))
            return new PublicSafeCheckOutput(true, flags);

        foreach (Match m in SecretTokenRegex().Matches(markdown))
        {
            flags.Add(new PublicSafeFlag("CredentialOrSecret", MaskSnippet(m.Value), "Detected token, API key, or password assignment."));
        }

        foreach (Match m in InternalUrlRegex().Matches(markdown))
        {
            flags.Add(new PublicSafeFlag("InternalUrl", m.Value, "Detected non-public internal or corporate URL."));
        }

        foreach (Match m in EmailPiiRegex().Matches(markdown))
        {
            if (!m.Value.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase)
                && !m.Value.EndsWith("@users.noreply.github.com", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add(new PublicSafeFlag("PersonalData", m.Value, "Detected non-synthetic email address."));
            }
        }

        return new PublicSafeCheckOutput(flags.Count == 0, flags);
    }

    public Task<SpecClarificationOutput> AnalyzeSpecForIntakeAsync(
        string specMarkdown,
        string? modelDeployment = null,
        CancellationToken ct = default)
    {
        const string schema = """
            {
              "type": "object",
              "required": ["clarifiedSpecMarkdown", "requirements", "clarifyingQuestions", "publicSafe"],
              "properties": {
                "clarifiedSpecMarkdown": { "type": "string" },
                "requirements": { "type": "array" },
                "clarifyingQuestions": { "type": "array", "items": { "type": "string" } },
                "publicSafe": { "type": "object", "required": ["passed", "flags"] }
              }
            }
            """;

        return ExecuteWithSchemaRetryAndFallbackAsync(
            operation: "AnalyzeSpecForIntake",
            promptFile: "spec-analysis.md",
            userPrompt: $"Analyze this specification and return JSON:\n\n{specMarkdown}",
            jsonSchemaText: schema,
            modelDeployment: modelDeployment,
            fallbackFactory: () => BuildDeterministicSpecAnalysis(specMarkdown),
            postProcess: result =>
            {
                // Merge deterministic public-safe guardrails so secrets/internal URLs are never missed
                var detSafe = CheckPublicSafeDeterministic(specMarkdown);
                var mergedFlags = result.PublicSafe.Flags
                    .Concat(detSafe.Flags)
                    .DistinctBy(f => $"{f.Category}:{f.Snippet}")
                    .ToList();
                return result with
                {
                    PublicSafe = new PublicSafeCheckOutput(mergedFlags.Count == 0, mergedFlags)
                };
            },
            ct: ct);
    }

    public Task<GeneratedPlanOutput> GeneratePlanAsync(
        string projectName,
        string clarifiedSpec,
        string templateId,
        string? modelDeployment = null,
        CancellationToken ct = default)
    {
        const string schema = """
            {
              "type": "object",
              "required": ["adr0001Markdown", "requirements", "milestones", "items", "estimatedCostUsd", "estimatedDurationMinutes"],
              "properties": {
                "adr0001Markdown": { "type": "string", "minLength": 10 },
                "requirements": { "type": "array", "minItems": 1 },
                "milestones": { "type": "array", "items": { "type": "string" } },
                "items": { "type": "array", "minItems": 1 },
                "estimatedCostUsd": { "type": "number" },
                "estimatedDurationMinutes": { "type": "integer" }
              }
            }
            """;

        return ExecuteWithSchemaRetryAndFallbackAsync(
            operation: "GeneratePlan",
            promptFile: "plan-generation.md",
            userPrompt: $"Project: {projectName}\nStack template: {templateId}\nSpecification:\n{clarifiedSpec}",
            jsonSchemaText: schema,
            modelDeployment: modelDeployment,
            fallbackFactory: () => BuildDeterministicPlan(projectName, clarifiedSpec, templateId),
            ct: ct);
    }

    public Task<PlanIncrementOutput> PlanIncrementAsync(
        string featureTitle,
        string featureSpec,
        ProjectIncrementContext context,
        string? modelDeployment = null,
        CancellationToken ct = default)
    {
        const string schema = """
            {
              "type": "object",
              "required": ["featureSlug", "newRequirements", "items", "overlapWarnings", "affectedInFlightIssueNumbers", "estimatedCostUsd", "estimatedDurationMinutes"],
              "properties": {
                "featureSlug": { "type": "string", "minLength": 2 },
                "newRequirements": { "type": "array", "minItems": 1 },
                "items": { "type": "array", "minItems": 1 },
                "overlapWarnings": { "type": "array", "items": { "type": "string" } },
                "affectedInFlightIssueNumbers": { "type": "array", "items": { "type": "integer" } },
                "estimatedCostUsd": { "type": "number" },
                "estimatedDurationMinutes": { "type": "integer" }
              }
            }
            """;

        return ExecuteWithSchemaRetryAndFallbackAsync(
            operation: "PlanIncrement",
            promptFile: "plan-generation.md",
            userPrompt: $"Feature: {featureTitle}\nNext REQ#: {context.NextRequirementNumber}\nSpec:\n{featureSpec}",
            jsonSchemaText: schema,
            modelDeployment: modelDeployment,
            fallbackFactory: () => BuildDeterministicIncrement(featureTitle, featureSpec, context),
            ct: ct);
    }

    public Task<ChangeProposalOutput> ProposeChangesAsync(
        string oldSpec,
        string newSpec,
        IReadOnlyList<BacklogSummaryItem> backlog,
        string? modelDeployment = null,
        CancellationToken ct = default)
    {
        const string schema = """
            {
              "type": "object",
              "required": ["summary", "items", "affectedInProgressIssues"],
              "properties": {
                "summary": { "type": "string" },
                "items": { "type": "array" },
                "affectedInProgressIssues": { "type": "array", "items": { "type": "integer" } }
              }
            }
            """;

        return ExecuteWithSchemaRetryAndFallbackAsync(
            operation: "ProposeChanges",
            promptFile: "spec-analysis.md",
            userPrompt: $"Compare old spec and new spec and produce ChangeProposalOutput:\nOLD:\n{oldSpec}\nNEW:\n{newSpec}",
            jsonSchemaText: schema,
            modelDeployment: modelDeployment,
            fallbackFactory: () => BuildDeterministicChangeProposal(oldSpec, newSpec, backlog),
            ct: ct);
    }

    public Task<InboxGroupingOutput> GroupInboxAsync(
        IReadOnlyList<UntrustedInboxIssue> issues,
        string? modelDeployment = null,
        CancellationToken ct = default)
    {
        const string schema = """
            {
              "type": "object",
              "required": ["suggestions"],
              "properties": { "suggestions": { "type": "array" } }
            }
            """;

        var quotedPayload = FormatUntrustedIssuesBlock(issues);
        return ExecuteWithSchemaRetryAndFallbackAsync(
            operation: "GroupInbox",
            promptFile: "inbox-and-features.md",
            userPrompt: $"Group these reported issues (advisory only):\n{quotedPayload}",
            jsonSchemaText: schema,
            modelDeployment: modelDeployment,
            fallbackFactory: () => BuildDeterministicInboxGroups(issues),
            ct: ct);
    }

    public Task<DraftedFeatureOutput> DraftFeatureFromIssuesAsync(
        IReadOnlyList<UntrustedInboxIssue> issues,
        string? modelDeployment = null,
        CancellationToken ct = default)
    {
        const string schema = """
            {
              "type": "object",
              "required": ["title", "slug", "featureSpecMarkdown", "sourceIssueNumbers"],
              "properties": {
                "title": { "type": "string", "minLength": 3 },
                "slug": { "type": "string", "minLength": 2 },
                "featureSpecMarkdown": { "type": "string", "minLength": 10 },
                "sourceIssueNumbers": { "type": "array", "items": { "type": "integer" } }
              }
            }
            """;

        var quotedPayload = FormatUntrustedIssuesBlock(issues);
        return ExecuteWithSchemaRetryAndFallbackAsync(
            operation: "DraftFeatureFromIssues",
            promptFile: "inbox-and-features.md",
            userPrompt: $"Draft a clean feature specification summarizing the product asks in these untrusted issues:\n{quotedPayload}",
            jsonSchemaText: schema,
            modelDeployment: modelDeployment,
            fallbackFactory: () => BuildDeterministicFeatureDraft(issues),
            ct: ct);
    }

    public Task<SpecReviewOutput> ReviewSpecAsync(
        IssueSpecInput issue,
        string? modelDeployment = null,
        CancellationToken ct = default)
    {
        const string schema = """
            {
              "type": "object",
              "required": ["verdict", "missing", "questions"],
              "properties": {
                "verdict": { "type": "string", "enum": ["Ready", "NeedsClarification"] },
                "missing": { "type": "array", "items": { "type": "string" } },
                "questions": { "type": "array", "items": { "type": "string" } }
              }
            }
            """;

        return ExecuteWithSchemaRetryAndFallbackAsync(
            operation: "ReviewSpec",
            promptFile: "spec-analysis.md",
            userPrompt: JsonSerializer.Serialize(issue, JsonOpts),
            jsonSchemaText: schema,
            modelDeployment: modelDeployment,
            fallbackFactory: () =>
            {
                var missing = new List<string>();
                var questions = new List<string>();
                if (string.IsNullOrWhiteSpace(issue.BodyMarkdown) || issue.BodyMarkdown.Trim().Length < 15)
                {
                    missing.Add("Problem statement");
                    questions.Add("What user or API problem should this work item solve, and what is out of scope?");
                }
                if (issue.AcceptanceCriteria.Count == 0 && !issue.BodyMarkdown.Contains("Acceptance Criteria", StringComparison.OrdinalIgnoreCase))
                {
                    missing.Add("Testable acceptance criteria");
                    questions.Add("Please provide testable acceptance criteria so acceptance tests and the review gate can verify completion.");
                }
                var verdict = missing.Count == 0 ? "Ready" : "NeedsClarification";
                return new SpecReviewOutput(verdict, missing, questions);
            },
            ct: ct);
    }

    public Task<TouchSetEstimateOutput> EstimateTouchSetAsync(
        IssueSpecInput issue,
        IReadOnlyList<string> repoTreePaths,
        string? modelDeployment = null,
        CancellationToken ct = default)
    {
        const string schema = """
            {
              "type": "object",
              "required": ["paths", "modules", "confidence"],
              "properties": {
                "paths": { "type": "array", "items": { "type": "string" } },
                "modules": { "type": "array", "items": { "type": "string" } },
                "confidence": { "type": "number", "minimum": 0, "maximum": 1 }
              }
            }
            """;

        return ExecuteWithSchemaRetryAndFallbackAsync(
            operation: "EstimateTouchSet",
            promptFile: "plan-generation.md",
            userPrompt: $"Issue #{issue.IssueNumber}: {issue.Title}\nTree sample: {string.Join(", ", repoTreePaths.Take(50))}",
            jsonSchemaText: schema,
            modelDeployment: modelDeployment,
            fallbackFactory: () =>
            {
                var words = Regex.Split(issue.Title.ToLowerInvariant(), @"[^a-z0-9]+").Where(w => w.Length >= 4).ToList();
                var matches = repoTreePaths
                    .Where(p => words.Any(w => p.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    .Take(5)
                    .ToList();
                if (matches.Count == 0)
                    matches.Add($"src/Features/Issue{issue.IssueNumber}/");
                var modules = matches.Select(p => p.Split('/')[0]).Distinct().ToList();
                return new TouchSetEstimateOutput(matches, modules, 0.72);
            },
            ct: ct);
    }

    public Task<ComposedInstructionsOutput> ComposeInstructionsAsync(
        IssueSpecInput issue,
        string? acceptanceContractMarkdown,
        string? modelDeployment = null,
        CancellationToken ct = default)
    {
        const string schema = """
            {
              "type": "object",
              "required": ["customInstructions", "definitionOfDoneChecklist"],
              "properties": {
                "customInstructions": { "type": "string", "minLength": 10 },
                "definitionOfDoneChecklist": { "type": "array", "items": { "type": "string" } }
              }
            }
            """;

        return ExecuteWithSchemaRetryAndFallbackAsync(
            operation: "ComposeInstructions",
            promptFile: "plan-generation.md",
            userPrompt: $"Compose factory-coder assignment instructions for #{issue.IssueNumber}: {issue.Title}",
            jsonSchemaText: schema,
            modelDeployment: modelDeployment,
            fallbackFactory: () =>
            {
                var checklist = new List<string>
                {
                    "Implement only the scope specified in this issue.",
                    "Do NOT modify protected paths (.github/**, scripts/**, docs/spec.md, docs/features/**, tests/acceptance/_harness/**, or locked AT folders).",
                    "Remove this issue's ID from tests/acceptance/pending.json once acceptance tests pass.",
                    "Run ./scripts/quality and ensure build, lint, and tests pass cleanly."
                };

                var sb = new StringBuilder();
                sb.AppendLine($"## Scope — Issue #{issue.IssueNumber}: {issue.Title}");
                sb.AppendLine(issue.BodyMarkdown);
                sb.AppendLine();
                sb.AppendLine("## Acceptance Criteria");
                foreach (var ac in issue.AcceptanceCriteria)
                    sb.AppendLine($"- {ac}");

                if (!string.IsNullOrWhiteSpace(acceptanceContractMarkdown))
                {
                    sb.AppendLine();
                    sb.AppendLine("## Locked Acceptance Interface Contract");
                    sb.AppendLine(acceptanceContractMarkdown);
                }

                sb.AppendLine();
                sb.AppendLine("## Definition of Done");
                foreach (var c in checklist)
                    sb.AppendLine($"- [ ] {c}");

                return new ComposedInstructionsOutput(sb.ToString(), checklist);
            },
            ct: ct);
    }

    public Task<FindingTriageOutput> TriageFindingsAsync(
        IReadOnlyList<TrackedFinding> findings,
        string diffSummary,
        string? modelDeployment = null,
        CancellationToken ct = default)
    {
        const string schema = """
            {
              "type": "object",
              "required": ["items"],
              "properties": { "items": { "type": "array" } }
            }
            """;

        return ExecuteWithSchemaRetryAndFallbackAsync(
            operation: "TriageFindings",
            promptFile: "triage-and-conformance.md",
            userPrompt: JsonSerializer.Serialize(new { findings, diffSummary }, JsonOpts),
            jsonSchemaText: schema,
            modelDeployment: modelDeployment,
            fallbackFactory: () => new FindingTriageOutput(
                findings.Select(f => new FindingTriageItem(
                    f.Id,
                    f.Status == FindingStatus.Contested ? "likely_false_positive" : "actionable",
                    f.Status == FindingStatus.Contested
                        ? "Repeated across rounds after fix commit; review justification."
                        : $"Copilot review {f.Severity} finding at {f.Path}.")).ToList()),
            ct: ct);
    }

    public Task<SpecConformanceOutput> CheckConformanceAsync(
        IssueSpecInput issue,
        string diffSummary,
        string? modelDeployment = null,
        CancellationToken ct = default)
    {
        const string schema = """
            {
              "type": "object",
              "required": ["verdict", "unmetCriteria"],
              "properties": {
                "verdict": { "type": "string", "enum": ["pass", "fail"] },
                "unmetCriteria": { "type": "array", "items": { "type": "string" } }
              }
            }
            """;

        return ExecuteWithSchemaRetryAndFallbackAsync(
            operation: "CheckConformance",
            promptFile: "triage-and-conformance.md",
            userPrompt: JsonSerializer.Serialize(new { issue, diffSummary }, JsonOpts),
            jsonSchemaText: schema,
            modelDeployment: modelDeployment,
            fallbackFactory: () =>
                string.IsNullOrWhiteSpace(diffSummary)
                    ? new SpecConformanceOutput("fail", ["Diff is empty; acceptance criteria are not implemented."])
                    : new SpecConformanceOutput("pass", []),
            ct: ct);
    }

    public Task<DecompositionSuggestionOutput> SuggestDecompositionAsync(
        IssueSpecInput issue,
        string? modelDeployment = null,
        CancellationToken ct = default)
    {
        const string schema = """
            {
              "type": "object",
              "required": ["subIssues"],
              "properties": { "subIssues": { "type": "array", "minItems": 1 } }
            }
            """;

        return ExecuteWithSchemaRetryAndFallbackAsync(
            operation: "SuggestDecomposition",
            promptFile: "plan-generation.md",
            userPrompt: JsonSerializer.Serialize(issue, JsonOpts),
            jsonSchemaText: schema,
            modelDeployment: modelDeployment,
            fallbackFactory: () => new DecompositionSuggestionOutput(
            [
                new DecomposedSubIssue(
                    $"{issue.Title} — API & domain slice",
                    $"Backend API and domain logic slice decomposed from #{issue.IssueNumber}.",
                    issue.AcceptanceCriteria.Take(Math.Max(1, issue.AcceptanceCriteria.Count / 2)).ToList(),
                    ItemSize.Small),
                new DecomposedSubIssue(
                    $"{issue.Title} — UI & integration slice",
                    $"Frontend UI slice decomposed from #{issue.IssueNumber}.",
                    issue.AcceptanceCriteria.Skip(Math.Max(1, issue.AcceptanceCriteria.Count / 2)).DefaultIfEmpty("UI integrates cleanly with backend API.").ToList(),
                    ItemSize.Small)
            ]),
            ct: ct);
    }

    public Task<ReorderOutput> SuggestReorderAsync(
        IReadOnlyList<BacklogSummaryItem> readySet,
        string? modelDeployment = null,
        CancellationToken ct = default)
    {
        const string schema = """
            {
              "type": "object",
              "required": ["suggestions"],
              "properties": { "suggestions": { "type": "array" } }
            }
            """;

        return ExecuteWithSchemaRetryAndFallbackAsync(
            operation: "SuggestReorder",
            promptFile: "plan-generation.md",
            userPrompt: JsonSerializer.Serialize(readySet, JsonOpts),
            jsonSchemaText: schema,
            modelDeployment: modelDeployment,
            fallbackFactory: () => new ReorderOutput([]),
            ct: ct);
    }

    /// <summary>
    /// Core execution wrapper implementing §3.2:
    /// Calls Foundry MAF agent -> validates output against JSON Schema -> retries ONCE if invalid -> deterministic fallback.
    /// Records prompt version + model deployment trace via IPmTraceSink (§9.1).
    /// </summary>
    public async Task<T> ExecuteWithSchemaRetryAndFallbackAsync<T>(
        string operation,
        string promptFile,
        string userPrompt,
        string jsonSchemaText,
        string? modelDeployment,
        Func<T> fallbackFactory,
        Func<T, T>? postProcess = null,
        CancellationToken ct = default)
    {
        var deployment = string.IsNullOrWhiteSpace(modelDeployment) ? options.Value.DefaultDeployment : modelDeployment;
        var systemPrompt = LoadCombinedInstructions(promptFile);
        var schema = JsonSchema.FromText(jsonSchemaText);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var promptToSend = attempt == 1
                    ? userPrompt
                    : $"{userPrompt}\n\nIMPORTANT RETRY: Your previous output failed JSON schema validation. Return strictly valid JSON matching:\n{jsonSchemaText}";

                var rawJson = StripCodeFences(await transport.InvokeJsonAsync(systemPrompt, promptToSend, deployment, ct));
                using var doc = JsonDocument.Parse(rawJson);
                var eval = schema.Evaluate(doc.RootElement);
                if (eval.IsValid)
                {
                    var parsed = JsonSerializer.Deserialize<T>(rawJson, JsonOpts);
                    if (parsed is not null)
                    {
                        var finalValue = postProcess is not null ? postProcess(parsed) : parsed;
                        await traceSink.RecordTraceAsync(new PmExecutionTrace(
                            operation,
                            options.Value.PromptVersion,
                            deployment,
                            UsedDeterministicFallback: false,
                            Attempts: attempt,
                            OutputJson: JsonSerializer.Serialize(finalValue, JsonOpts),
                            Timestamp: DateTimeOffset.UtcNow), ct);
                        return finalValue;
                    }
                }

                logger.LogWarning("PM agent output failed JSON schema validation on attempt {Attempt} for {Operation}.", attempt, operation);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "PM model invocation attempt {Attempt} for {Operation} fell through.", attempt, operation);
                if (ex is InvalidOperationException)
                    break; // Transport not configured -> go straight to deterministic fallback
            }
        }

        var fallback = fallbackFactory();
        var processedFallback = postProcess is not null ? postProcess(fallback) : fallback;
        await traceSink.RecordTraceAsync(new PmExecutionTrace(
            operation,
            options.Value.PromptVersion,
            deployment,
            UsedDeterministicFallback: true,
            Attempts: 2,
            OutputJson: JsonSerializer.Serialize(processedFallback, JsonOpts),
            Timestamp: DateTimeOffset.UtcNow), ct);

        return processedFallback;
    }

    private string LoadCombinedInstructions(string promptFile)
    {
        var baseInstr = FoundryAgentFrameworkTransport.LoadSystemInstructions(options.Value.PromptsDirectory);
        var subPath = Path.Combine(options.Value.PromptsDirectory, promptFile);
        if (File.Exists(subPath))
            return $"{baseInstr}\n\n{File.ReadAllText(subPath)}";

        var rootSub = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", options.Value.PromptsDirectory, promptFile));
        if (File.Exists(rootSub))
            return $"{baseInstr}\n\n{File.ReadAllText(rootSub)}";

        return baseInstr;
    }

    private static string StripCodeFences(string raw)
    {
        var trimmed = raw.Trim();

        // Strip internal chain-of-thought (<think>...</think>) emitted by reasoning models such as MAI-Thinking-1
        var thinkEnd = trimmed.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkEnd >= 0)
            trimmed = trimmed[(thinkEnd + "</think>".Length)..].Trim();

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            if (firstNewLine > 0)
                trimmed = trimmed[(firstNewLine + 1)..];
            if (trimmed.EndsWith("```", StringComparison.Ordinal))
                trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }

        // If reasoning prose precedes or trails the JSON object, isolate the outermost JSON object
        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            return trimmed[firstBrace..(lastBrace + 1)];

        return trimmed;
    }

    private static string FormatUntrustedIssuesBlock(IReadOnlyList<UntrustedInboxIssue> issues)
    {
        var sb = new StringBuilder();
        foreach (var i in issues)
        {
            sb.AppendLine($"<untrusted_issue_data number=\"{i.IssueNumber}\" author=\"{i.Author}\">");
            sb.AppendLine($"Title: {i.Title}");
            sb.AppendLine($"Body: {i.Body}");
            sb.AppendLine("</untrusted_issue_data>");
        }
        return sb.ToString();
    }

    private SpecClarificationOutput BuildDeterministicSpecAnalysis(string specMarkdown)
    {
        var publicSafe = CheckPublicSafeDeterministic(specMarkdown);
        var bullets = ExtractBulletLines(specMarkdown);
        if (bullets.Count == 0)
        {
            bullets.Add("Deliver core interactive demo experience with synthetic seed data and health endpoint.");
            bullets.Add("Render responsive Fluent UI dashboard with dark and light mode.");
        }

        var reqs = bullets.Select((text, i) =>
        {
            var existingReq = Regex.Match(text, @"\b(REQ-\d{3,})\b");
            var id = existingReq.Success ? existingReq.Groups[1].Value : $"REQ-{i + 1:D3}";
            var cleaned = Regex.Replace(text, @"^\*\*?REQ-\d{3,}\*\*?\s*:?\s*", string.Empty).Trim();
            return new ExtractedRequirement(id, cleaned);
        }).ToList();

        var questions = new List<string>();
        if (!specMarkdown.Contains("auth", StringComparison.OrdinalIgnoreCase))
            questions.Add("Should the demo environment use anonymous synthetic personas by default, or include a simulated role switcher?");
        if (bullets.Count < 3)
            questions.Add("Are there any specific KPI charts or export formats you want highlighted during live demos?");

        var sb = new StringBuilder();
        sb.AppendLine(specMarkdown.Trim());
        if (!specMarkdown.Contains("REQ-001", StringComparison.Ordinal))
        {
            sb.AppendLine();
            sb.AppendLine("## Traceable Requirements");
            foreach (var r in reqs)
                sb.AppendLine($"- **{r.Id}**: {r.Text}");
        }

        return new SpecClarificationOutput(sb.ToString(), reqs, questions, publicSafe);
    }

    private static GeneratedPlanOutput BuildDeterministicPlan(string projectName, string clarifiedSpec, string templateId)
    {
        var bullets = ExtractBulletLines(clarifiedSpec);
        if (bullets.Count == 0)
        {
            bullets.Add("Core API & synthetic demo dataset");
            bullets.Add("Interactive Fluent UI dashboard & live view");
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nextNum = 1;
        var reqs = new List<ExtractedRequirement>();
        foreach (var b in bullets)
        {
            if (reqs.Count >= 6)
                break;

            var m = Regex.Match(b, @"\b(REQ-\d{3,})\b");
            var text = Regex.Replace(b, @"^\*\*?REQ-\d{3,}\*\*?\s*:?\s*", string.Empty).Trim();
            if (reqs.Any(existing => string.Equals(existing.Text, text, StringComparison.OrdinalIgnoreCase)))
                continue;

            string id;
            if (m.Success && usedIds.Add(m.Groups[1].Value))
            {
                id = m.Groups[1].Value;
            }
            else
            {
                while (!usedIds.Add($"REQ-{nextNum:D3}"))
                    nextNum++;
                id = $"REQ-{nextNum:D3}";
            }

            reqs.Add(new ExtractedRequirement(id, text));
        }

        var items = new List<PlannedBacklogItem>
        {
            new(
                Key: "ISS-1",
                Title: $"Walking skeleton: {projectName} service, synthetic seed & /healthz",
                Description: $"Bootstrap {projectName} on `{templateId}` with synthetic demo data, health probe, and green CI.",
                AcceptanceCriteria: ["GET /healthz returns 200 OK", "Synthetic seed dataset loads deterministically on startup"],
                RequirementIds: [reqs[0].Id],
                Size: ItemSize.Small,
                Priority: PriorityLevel.Critical,
                Milestone: "M1: Walking Skeleton",
                IsAcceptanceTest: false,
                BlockedByKeys: [],
                BlockedByExistingIssueNumbers: [],
                PredictedTouchPaths: ["src/Api/Program.cs", "src/Api/Seed/"])
        };

        var keyCounter = 2;
        foreach (var req in reqs)
        {
            var atKey = $"ISS-{keyCounter++}";
            var featKey = $"ISS-{keyCounter++}";

            items.Add(new PlannedBacklogItem(
                Key: atKey,
                Title: $"AT: {TruncateTitle(req.Text)} ({req.Id})",
                Description: $"Write executable acceptance tests and `contract.md` under `tests/acceptance/{featKey}/` for **{req.Id}**.",
                AcceptanceCriteria:
                [
                    $"Test `{req.Id}_acceptance` fails on assertion before feature implementation",
                    $"`tests/acceptance/{featKey}/contract.md` defines routes, schemas, and `data-testid` hooks"
                ],
                RequirementIds: [req.Id],
                Size: ItemSize.Small,
                Priority: PriorityLevel.High,
                Milestone: "M2: Core Demo Capabilities",
                IsAcceptanceTest: true,
                BlockedByKeys: ["ISS-1"],
                BlockedByExistingIssueNumbers: [],
                PredictedTouchPaths: [$"tests/acceptance/{featKey}/"]));

            items.Add(new PlannedBacklogItem(
                Key: featKey,
                Title: $"Implement {TruncateTitle(req.Text)} ({req.Id})",
                Description: $"Implement **{req.Id}**: {req.Text}",
                AcceptanceCriteria:
                [
                    req.Text,
                    $"Locked acceptance suite `{featKey}` passes without modifying test files",
                    $"`{featKey}` removed from `tests/acceptance/pending.json`"
                ],
                RequirementIds: [req.Id],
                Size: ItemSize.Medium,
                Priority: PriorityLevel.Medium,
                Milestone: "M2: Core Demo Capabilities",
                IsAcceptanceTest: false,
                BlockedByKeys: [atKey],
                BlockedByExistingIssueNumbers: [],
                PredictedTouchPaths: [$"src/Features/{req.Id}/", $"web/src/features/{req.Id.ToLowerInvariant()}/"]));
        }

        var adr = $"""
            # ADR-0001: {projectName} Architecture

            - **Status:** Accepted
            - **Stack Template:** `{templateId}`
            - **Deployment Target:** Azure Container Apps (`preview` per PR + `demo` on `main`, OIDC federated credentials, zero stored secrets)
            - **Data Hygiene:** Synthetic seeded data only; public-safe verified.
            """;

        var cost = Math.Round(items.Count * 0.65m, 2);
        var duration = items.Count * 14;
        return new GeneratedPlanOutput(adr, reqs, ["M1: Walking Skeleton", "M2: Core Demo Capabilities"], items, cost, duration);
    }

    private static PlanIncrementOutput BuildDeterministicIncrement(
        string featureTitle,
        string featureSpec,
        ProjectIncrementContext context)
    {
        var slug = Slugify(featureTitle);
        var bullets = ExtractBulletLines(featureSpec);
        if (bullets.Count == 0)
            bullets.Add(featureTitle);

        var newReqs = bullets.Take(4).Select((b, idx) =>
            new ExtractedRequirement($"REQ-{context.NextRequirementNumber + idx:D3}", b)).ToList();

        var overlaps = new List<string>();
        foreach (var existing in context.ExistingBacklog)
        {
            if (existing.Title.Contains(featureTitle, StringComparison.OrdinalIgnoreCase))
            {
                overlaps.Add($"Potential overlap with existing issue #{existing.IssueNumber} ('{existing.Title}').");
            }
        }

        var items = new List<PlannedBacklogItem>();
        var seq = 1;
        foreach (var req in newReqs)
        {
            var atKey = $"INC-{seq++}";
            var implKey = $"INC-{seq++}";

            items.Add(new PlannedBacklogItem(
                Key: atKey,
                Title: $"AT: {TruncateTitle(req.Text)} ({req.Id})",
                Description: $"Author executable acceptance tests and `contract.md` for `{req.Id}` (`feature:{slug}`).",
                AcceptanceCriteria: [$"New acceptance test for {req.Id} fails cleanly on assertion", "`contract.md` committed"],
                RequirementIds: [req.Id],
                Size: ItemSize.Small,
                Priority: PriorityLevel.High,
                Milestone: $"Feature: {featureTitle}",
                IsAcceptanceTest: true,
                BlockedByKeys: [],
                BlockedByExistingIssueNumbers: [],
                PredictedTouchPaths: [$"tests/acceptance/{slug}-{req.Id}/"]));

            items.Add(new PlannedBacklogItem(
                Key: implKey,
                Title: $"{TruncateTitle(req.Text)} ({req.Id})",
                Description: $"Implement `{req.Id}` for feature `{slug}`: {req.Text}",
                AcceptanceCriteria: [req.Text, $"Acceptance suite for {req.Id} passes"],
                RequirementIds: [req.Id],
                Size: ItemSize.Medium,
                Priority: PriorityLevel.Medium,
                Milestone: $"Feature: {featureTitle}",
                IsAcceptanceTest: false,
                BlockedByKeys: [atKey],
                BlockedByExistingIssueNumbers: [],
                PredictedTouchPaths: [$"src/Features/{slug}/", $"web/src/features/{slug}/"]));
        }

        return new PlanIncrementOutput(
            FeatureSlug: slug,
            AdrMarkdown: null,
            NewRequirements: newReqs,
            Items: items,
            OverlapWarnings: overlaps,
            AffectedInFlightIssueNumbers: [],
            EstimatedCostUsd: Math.Round(items.Count * 0.60m, 2),
            EstimatedDurationMinutes: items.Count * 12);
    }

    private static ChangeProposalOutput BuildDeterministicChangeProposal(
        string oldSpec,
        string newSpec,
        IReadOnlyList<BacklogSummaryItem> backlog)
    {
        var oldLines = ExtractBulletLines(oldSpec).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newLines = ExtractBulletLines(newSpec).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = newLines.Except(oldLines, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = oldLines.Except(newLines, StringComparer.OrdinalIgnoreCase).ToList();

        var proposals = new List<ProposedChangeItem>();
        foreach (var line in added)
        {
            proposals.Add(new ProposedChangeItem("add", null, $"Add capability: {TruncateTitle(line)}", line, []));
        }

        foreach (var line in removed)
        {
            var linked = backlog.FirstOrDefault(b => line.Contains(b.Title, StringComparison.OrdinalIgnoreCase));
            if (linked is { Status: BoardStatus.Done })
            {
                proposals.Add(new ProposedChangeItem("change-request", linked.IssueNumber, $"CR: Update completed work from #{linked.IssueNumber}", $"Spec removed/changed: {line}", linked.RequirementIds));
            }
            else if (linked is not null)
            {
                proposals.Add(new ProposedChangeItem("close", linked.IssueNumber, $"Close obsolete issue #{linked.IssueNumber}", line, linked.RequirementIds));
            }
        }

        if (proposals.Count == 0)
        {
            proposals.Add(new ProposedChangeItem("update", backlog.FirstOrDefault()?.IssueNumber, "Refresh acceptance criteria to match edited specification", "Synchronize wording with updated spec version.", []));
        }

        var affectedInProgress = backlog
            .Where(b => b.Status is BoardStatus.InProgress or BoardStatus.InReview)
            .Select(b => b.IssueNumber)
            .ToList();

        return new ChangeProposalOutput(
            $"Spec delta: {added.Count} requirement line(s) added, {removed.Count} modified/removed.",
            proposals,
            affectedInProgress);
    }

    private static InboxGroupingOutput BuildDeterministicInboxGroups(IReadOnlyList<UntrustedInboxIssue> issues)
    {
        var list = new List<InboxSuggestionItem>();
        var seenByGroup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var issue in issues)
        {
            var text = $"{issue.Title} {issue.Body}".ToLowerInvariant();
            var group = text.Contains("contrast") || text.Contains("dark") || text.Contains("keyboard") || text.Contains("a11y")
                ? "Accessibility & Theme Polish"
                : text.Contains("csv") || text.Contains("export") || text.Contains("download")
                    ? "Export & Reporting"
                    : "General Enhancements";

            int? dup = seenByGroup.TryGetValue(group, out var first) && issue.Title.Split(' ').Length <= 6 ? first : null;
            seenByGroup.TryAdd(group, issue.IssueNumber);
            list.Add(new InboxSuggestionItem(issue.IssueNumber, group, dup));
        }

        return new InboxGroupingOutput(list);
    }

    private static DraftedFeatureOutput BuildDeterministicFeatureDraft(IReadOnlyList<UntrustedInboxIssue> issues)
    {
        var primary = issues.FirstOrDefault()?.Title ?? "Community Feedback Enhancement";
        var cleanTitle = Regex.Replace(primary, @"[^\w\s\-]", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
            cleanTitle = "Community Enhancement";

        var slug = Slugify(cleanTitle);
        var sb = new StringBuilder();
        sb.AppendLine($"# Feature: {cleanTitle}");
        sb.AppendLine();
        sb.AppendLine($"Synthesized from reported inbox issue(s): {string.Join(", ", issues.Select(i => $"#{i.IssueNumber}"))}.");
        sb.AppendLine();
        sb.AppendLine("## Requirements & Acceptance Criteria");
        foreach (var i in issues)
        {
            var safeSummary = Regex.Replace(i.Title, @"[\r\n`<>]", " ").Trim();
            sb.AppendLine($"- Deliver capability addressing feedback in #{i.IssueNumber}: {safeSummary}");
        }

        return new DraftedFeatureOutput(cleanTitle, slug, sb.ToString(), issues.Select(i => i.IssueNumber).ToList());
    }

    private static List<string> ExtractBulletLines(string markdown) =>
        markdown.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal) || l.StartsWith("* ", StringComparison.Ordinal))
            .Select(l => l[2..].Trim())
            .Where(l => l.Length > 3 && !l.StartsWith("[0", StringComparison.Ordinal))
            .ToList();

    private static string TruncateTitle(string text) =>
        text.Length <= 68 ? text : text[..65].TrimEnd() + "...";

    public static string Slugify(string input)
    {
        var slug = Regex.Replace(input.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "feature" : slug[..Math.Min(36, slug.Length)].TrimEnd('-');
    }

    private static string MaskSnippet(string s) =>
        s.Length <= 8 ? "***" : $"{s[..4]}...{s[^2..]}";

    [GeneratedRegex(@"(ghp_[A-Za-z0-9]{20,}|ghu_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9]{20,}|Password\s*=\s*[^\s;""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretTokenRegex();

    [GeneratedRegex(@"https?://[A-Za-z0-9.\-]*\.(internal|local|corp\.microsoft\.com|msft\.net)(/[^\s)\]]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex InternalUrlRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b")]
    private static partial Regex EmailPiiRegex();
}

public static class AgentsServiceCollectionExtensions
{
    public static IServiceCollection AddDemoFactoryAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<FoundryAgentOptions>(opts =>
        {
            var sec = configuration.GetSection("Foundry");
            opts.ProjectEndpoint = sec["ProjectEndpoint"] ?? opts.ProjectEndpoint;
            opts.DefaultDeployment = sec["DefaultDeployment"] ?? opts.DefaultDeployment;
            opts.Region = sec["Region"] ?? opts.Region;
            opts.PromptsDirectory = sec["PromptsDirectory"] ?? opts.PromptsDirectory;
            opts.PromptVersion = sec["PromptVersion"] ?? opts.PromptVersion;
            opts.ManagedIdentityClientId = sec["ManagedIdentityClientId"] ?? opts.ManagedIdentityClientId;
        });
        services.AddSingleton<FoundryAgentFrameworkTransport>();
        services.AddSingleton<IProjectManagerModelTransport>(sp => sp.GetRequiredService<FoundryAgentFrameworkTransport>());
        services.AddSingleton<IPmTraceSink, NullPmTraceSink>();
        services.AddSingleton<IProjectManagerAgent, ProjectManagerAgent>();
        return services;
    }
}
