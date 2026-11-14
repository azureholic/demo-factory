using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DemoFactory.Domain;

public sealed class RepoPolicy
{
    public string Repository { get; set; } = string.Empty;
    public ProjectBoardTarget Project { get; set; } = new();
    public AutonomyLevel Autonomy { get; set; } = AutonomyLevel.L1;
    public int Wip { get; set; } = 1;
    public int BoardPollSeconds { get; set; } = 60;
    public string WorkLabel { get; set; } = "factory";
    public bool PmCanReorder { get; set; } = false;
    public double TouchSetOverlapThreshold { get; set; } = 0.35;
    public bool HasDeployTarget { get; set; } = true;
    public IntakePolicy Intake { get; set; } = new();
    public PolicyModels Models { get; set; } = new();
    public ReviewPolicy Review { get; set; } = new();
    public GatePolicySettings Gate { get; set; } = new();
    public AcceptanceTestPolicy AcceptanceTests { get; set; } = new();
    public List<string> ProtectedPaths { get; set; } = DefaultProtectedPaths();
    public List<string> AllowedGitHubActions { get; set; } =
    [
        "actions/checkout",
        "actions/setup-dotnet",
        "actions/setup-node",
        "actions/setup-python",
        "actions/upload-artifact",
        "actions/download-artifact",
        "azure/login",
        "azure/container-apps-deploy-action",
        "github/codeql-action"
    ];
    public List<string> TrustedActors { get; set; } =
    [
        "owner",
        "copilot-swe-agent",
        "copilot-pull-request-reviewer",
        "dependabot"
    ];
    public BudgetPolicy Budget { get; set; } = new();
    public List<string> RiskLabels { get; set; } = ["area:auth", "area:infra", "deps"];

    public static List<string> DefaultProtectedPaths() =>
    [
        ".github/**",
        "scripts/**",
        "tests/acceptance/_harness/**",
        "docs/spec.md",
        "docs/features/**",
        "CODEOWNERS",
        ".editorconfig",
        "eslint.config.*",
        "**/*.ruleset.json"
    ];

    public static RepoPolicy ParseYaml(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var parsed = deserializer.Deserialize<RepoPolicy?>(yaml) ?? new RepoPolicy();
        if (parsed.ProtectedPaths.Count == 0)
            parsed.ProtectedPaths = DefaultProtectedPaths();
        if (parsed.Gate.RequiredChecks.Count == 0)
            parsed.Gate.RequiredChecks = ["build", "test", "lint"];
        return parsed;
    }

    public string ToYaml()
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        return serializer.Serialize(this);
    }
}

public sealed class ProjectBoardTarget
{
    public string Owner { get; set; } = string.Empty;
    public int Number { get; set; } = 1;
}

public sealed class IntakePolicy
{
    public bool AcceptOutsideIssues { get; set; } = true;
    public bool CloseSourceIssuesWhenDone { get; set; } = true;
}

public sealed class PolicyModels
{
    public string Coding { get; set; } = "gemini-4-carbon";
    public string Pm { get; set; } = "foundry/gpt-4.1";
}

public sealed class ReviewPolicy
{
    public FindingSeverity BlockingSeverity { get; set; } = FindingSeverity.Medium;
    public int MaxRounds { get; set; } = 3;
    public ContestedSeverityPolicy ContestedPolicy { get; set; } = new();
}

public sealed class ContestedSeverityPolicy
{
    public string Low { get; set; } = "allow";
    public string Medium { get; set; } = "allow";
    public string High { get; set; } = "block";
}

public sealed class GatePolicySettings
{
    public List<string> RequiredChecks { get; set; } = ["build", "test", "lint"];
    public int MaxChangedLines { get; set; } = 1500;
    public bool SpecConformance { get; set; } = true;
}

public sealed class AcceptanceTestPolicy
{
    public ItemSize SeparateIssueMinSize { get; set; } = ItemSize.Medium;
    public int MaxFixes { get; set; } = 2;
}

public sealed class BudgetPolicy
{
    public int DailySessions { get; set; } = 40;
    public int MaxSessionsPerItem { get; set; } = 5;
    public decimal DailySpendUsd { get; set; } = 25m;
    public decimal SpendPerItemUsd { get; set; } = 5m;
}

public sealed record ModelCatalogueItem(
    string Id,
    string DisplayName,
    string ApiModelId,
    bool Enabled,
    bool SmokeTestPassed,
    string? Notes = null);

public static class ModelResolver
{
    public const string DefaultModelId = "gemini-4-carbon";

    /// <summary>
    /// §9.4 & §16 S2: Resolution order is workItem -> repo -> global -> gemini-4-carbon.
    /// Falls back to the first enabled smoke-tested catalogue model, or "auto" if none are enabled.
    /// </summary>
    public static string ResolveCodingModel(
        string? workItemOverride,
        string? repoSetting,
        string? globalDefault,
        IReadOnlyList<ModelCatalogueItem> catalogue)
    {
        string?[] candidates =
        [
            workItemOverride,
            repoSetting,
            globalDefault,
            DefaultModelId
        ];

        if (catalogue.Count == 0)
        {
            return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? DefaultModelId;
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var entry = catalogue.FirstOrDefault(m =>
                string.Equals(m.Id, candidate, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.ApiModelId, candidate, StringComparison.OrdinalIgnoreCase));

            if (entry is { Enabled: true })
                return entry.ApiModelId;
        }

        var fallback = catalogue.FirstOrDefault(m => m.Enabled);
        return fallback?.ApiModelId ?? "auto";
    }
}
