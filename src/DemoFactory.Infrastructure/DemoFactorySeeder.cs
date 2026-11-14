using System.Text.Json;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using DemoFactory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace DemoFactory.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddDemoFactoryInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<FactoryEnvironmentOptions>(configuration.GetSection("Factory"));

        services.AddDbContextFactory<DemoFactoryDbContext>((sp, opts) =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var sqlConnection = cfg.GetConnectionString("DefaultConnection")
                ?? cfg.GetConnectionString("sqldb");
            var sqliteConnection = cfg.GetConnectionString("Sqlite")
                ?? "Data Source=demofactory.db";

            if (!string.IsNullOrWhiteSpace(sqlConnection))
            {
                opts.UseSqlServer(sqlConnection, sql => sql.EnableRetryOnFailure());
            }
            else
            {
                opts.UseSqlite(sqliteConnection);
            }
        });

        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<DemoFactoryDbContext>>().CreateDbContext());
        services.AddSingleton<IEvidenceStore, EvidenceStore>();
        services.AddSingleton<IWebhookEventBus, WebhookEventBus>();
        services.AddSingleton<IPolicyRepository, PolicyRepository>();
        services.AddSingleton<IAuditLogger, AuditLogger>();
        services.AddSingleton<IPreviewHostingService, PreviewHostingService>();

        services.AddLogging(logging =>
        {
            logging.AddOpenTelemetry(otel =>
            {
                otel.IncludeFormattedMessage = true;
                otel.IncludeScopes = true;
            });
        });

        var otelBuilder = services.AddOpenTelemetry()
            .WithTracing(t => t
                .AddSource("DemoFactory.*")
                .AddSource("Microsoft.Agents.AI*")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithMetrics(m => m
                .AddMeter("DemoFactory.*")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otelBuilder.UseOtlpExporter();
        }

        if (!string.IsNullOrWhiteSpace(configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            otelBuilder.UseAzureMonitor();
        }

        return services;
    }
}

public static class DemoFactorySeeder
{
    public static async Task EnsureSeededAsync(
        IDbContextFactory<DemoFactoryDbContext> dbFactory,
        string ownerLogin = "azureholic",
        bool seedDemoData = true,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);

        if (!await db.ModelCatalogue.AnyAsync(ct))
        {
            var now = DateTimeOffset.UtcNow;
            db.ModelCatalogue.AddRange(
                new ModelCatalogueEntity
                {
                    Id = "gemini-4-carbon",
                    DisplayName = "Gemini 4 Carbon",
                    ApiModelId = "gemini-4-carbon",
                    Enabled = true,
                    SmokeTestPassed = true,
                    LastSmokeTestedAt = now.AddHours(-3),
                    Notes = "Default coding model. Usage-based billing on Copilot Pro+ and Max."
                },
                new ModelCatalogueEntity
                {
                    Id = "claude-sonnet-5.5",
                    DisplayName = "Claude Sonnet 5.5",
                    ApiModelId = "claude-sonnet-5.5",
                    Enabled = true,
                    SmokeTestPassed = true,
                    LastSmokeTestedAt = now.AddHours(-3),
                    Notes = "Strong multi-file refactoring and Playwright test authoring."
                },
                new ModelCatalogueEntity
                {
                    Id = "gpt-5.3-codex",
                    DisplayName = "GPT-5.3 Codex",
                    ApiModelId = "gpt-5.3-codex",
                    Enabled = true,
                    SmokeTestPassed = true,
                    LastSmokeTestedAt = now.AddHours(-3),
                    Notes = "Fast .NET 11 / TypeScript code generation."
                },
                new ModelCatalogueEntity
                {
                    Id = "auto",
                    DisplayName = "Copilot Auto",
                    ApiModelId = "auto",
                    Enabled = true,
                    SmokeTestPassed = true,
                    LastSmokeTestedAt = now.AddHours(-3),
                    Notes = "GitHub-managed fallback model selection."
                });
        }

        if (!await db.AllowlistUsers.AnyAsync(ct))
        {
            db.AllowlistUsers.Add(new AllowlistUserEntity
            {
                GitHubLogin = ownerLogin,
                Role = UserRole.Admin,
                CopilotPlan = "Copilot Pro+",
                ThemePreference = "system",
                DensityPreference = "comfortable"
            });
        }

        if (!await db.RuntimeSettings.AnyAsync(s => s.Scope == SettingsScope.Global, ct))
        {
            db.RuntimeSettings.Add(new RuntimeSettingsEntity
            {
                Scope = SettingsScope.Global,
                CodingModel = "gemini-4-carbon",
                PmModelDeployment = "gpt-4.1-eu",
                Wip = 1,
                Autonomy = AutonomyLevel.L1,
                MaxRounds = 3,
                GlobalPaused = false,
                DailyDigestEnabled = true,
                UpdatedBy = ownerLogin
            });
        }

        if (!await db.EncryptedUserTokens.AnyAsync(ct))
        {
            db.EncryptedUserTokens.Add(new EncryptedUserTokenEntity
            {
                GitHubLogin = ownerLogin,
                EncryptedRefreshToken = "kv-wrapped:sandbox-refresh-token",
                KeyId = "kv://demo-factory-kv/keys/github-token-wrap",
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(7),
                RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddMonths(5),
                IsRevoked = false
            });
        }

        await db.SaveChangesAsync(ct);

        if (!seedDemoData || await db.Projects.AnyAsync(ct))
            return;

        // Seed a realistic showcase project ("Contoso Energy Grid Demo") so every §14.2 screen works out of the box
        var nowUtc = DateTimeOffset.UtcNow;
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var repoId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var featureId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var specMarkdown = """
            # Contoso Smart Grid Telemetry Demo

            Interactive demo showcasing real-time substation telemetry, tariff forecasting, and anomaly alerts.

            ## Requirements
            - **REQ-001**: Display live substation load kW and voltage health cards with synthetic seed telemetry.
            - **REQ-002**: Expose `GET /api/substations` and `GET /api/substations/{id}/metrics` with OpenAPI 3.2 contract.
            - **REQ-003**: Allow operators to acknowledge voltage-sag alerts with role-gated audit trail (`area:auth`).
            - **REQ-004**: Export 24-hour tariff comparison CSV and interactive chart (`feature:tariff-export`).

            ## Feature Index
            - [001 - Tariff Forecast Export](docs/features/001-tariff-export.md)
            """;

        var bootstrapSteps = JsonSerializer.Serialize(new[]
        {
            new { step = 1, name = "Intake & public-safe check", status = "Completed" },
            new { step = 5, name = "Create repo from template", status = "Completed" },
            new { step = 6, name = "Apply GitHub security baseline", status = "Completed" },
            new { step = 7, name = "Apply rulesets & Copilot review settings", status = "Completed" },
            new { step = 8, name = "Create Project v2 board", status = "Completed" },
            new { step = 9, name = "Create backlog & dependencies", status = "Completed" },
            new { step = 10, name = "Smoke test & start ProjectScheduler", status = "Completed" }
        });

        db.Projects.Add(new ProjectEntity
        {
            Id = projectId,
            Name = "Contoso Smart Grid Demo",
            Slug = "contoso-smart-grid",
            SpecMarkdown = specMarkdown,
            CurrentSpecVersion = 2,
            TemplateId = "dotnet-react",
            TemplateVersion = "v1.2.0",
            PlanApprovedBy = ownerLogin,
            PlanApprovedAt = nowUtc.AddDays(-4),
            BootstrapInstanceId = "bootstrap-contoso-smart-grid-v1",
            BootstrapStepsJson = bootstrapSteps,
            State = ProjectState.Running,
            DefaultAutonomy = AutonomyLevel.L1,
            DefaultCodingModel = "gemini-4-carbon",
            DemoUrl = "https://demo-contoso-smart-grid.swedencentral.azurecontainerapps.io",
            CreatedAt = nowUtc.AddDays(-4),
            UpdatedAt = nowUtc.AddMinutes(-12)
        });

        var readinessJson = JsonSerializer.Serialize(new[]
        {
            new { key = "copilot-review-ruleset", label = "Automatic Copilot code review ruleset (new pushes on, draft off)", passed = true, canAutoFix = true },
            new { key = "copilot-cloud-agent", label = "Copilot cloud agent enabled & workflow approval off", passed = true, canAutoFix = false },
            new { key = "pinned-required-checks", label = "Required checks pinned to GitHub Actions & Demo-Factory App", passed = true, canAutoFix = true },
            new { key = "workflow-permissions", label = "Read-only GITHUB_TOKEN, no repo secrets, fork approval on", passed = true, canAutoFix = true },
            new { key = "environments-main-only", label = "preview and demo environments restricted to main", passed = true, canAutoFix = true },
            new { key = "work-label-and-forms", label = "Work label 'factory' present, issue forms clean, auto-add off", passed = true, canAutoFix = true },
            new { key = "auto-merge-squash", label = "Auto-merge & squash-only enabled", passed = true, canAutoFix = true },
            new { key = "factory-coder-profile", label = ".github/agents/factory-coder.md and copilot-setup-steps.yml present", passed = true, canAutoFix = true }
        });

        db.Repositories.Add(new RepositoryEntity
        {
            Id = repoId,
            ProjectId = projectId,
            Owner = ownerLogin,
            Name = "contoso-smart-grid",
            InstallationId = 90421001,
            GitHubProjectId = "PVT_kwHOAA1234",
            GitHubProjectNumber = 7,
            PolicyReference = $"policies/repos/{ownerLogin}__contoso-smart-grid.yml",
            Paused = false,
            ReadinessPassed = true,
            ReadinessReportJson = readinessJson,
            CreatedAt = nowUtc.AddDays(-4)
        });

        db.LineStates.Add(new LineStateEntity
        {
            RepoId = repoId,
            State = LineStatus.Running,
            FlakyTestCount = 1,
            RestoredAt = nowUtc.AddDays(-1),
            LastRestoreDurationSeconds = 420
        });

        db.SpecVersions.AddRange(
            new SpecVersionEntity
            {
                ProjectId = projectId,
                Version = 1,
                GitSha = "a1b2c3d4e5f60001",
                Tag = "spec-v1",
                SpecMarkdown = "# Contoso Smart Grid Telemetry Demo\n\n- **REQ-001**: Display live substation load cards.\n- **REQ-002**: Expose substation REST endpoints.\n- **REQ-003**: Operator alert acknowledgement.",
                PublicSafeResultJson = "{\"passed\":true,\"flags\":[]}",
                CreatedAt = nowUtc.AddDays(-4)
            },
            new SpecVersionEntity
            {
                ProjectId = projectId,
                Version = 2,
                GitSha = "f6e5d4c3b2a10002",
                Tag = "spec-v2",
                SpecMarkdown = specMarkdown,
                PublicSafeResultJson = "{\"passed\":true,\"flags\":[]}",
                CreatedAt = nowUtc.AddDays(-1)
            });

        db.FeatureSpecs.Add(new FeatureSpecEntity
        {
            Id = featureId,
            ProjectId = projectId,
            Title = "Tariff Forecast CSV Export",
            Slug = "tariff-export",
            Path = "docs/features/001-tariff-export.md",
            SpecMarkdown = "# Feature 001: Tariff Forecast CSV Export\n\n- **REQ-004**: Operators can export 24-hour tariff comparison CSV.",
            State = FeatureSpecState.Applied,
            SpecVersion = 2,
            EpicIssueNumber = 10,
            SourceIssueNumbersJson = "[8]",
            Priority = PriorityLevel.Medium,
            Milestone = "v1.1 Showcase",
            CreatedAt = nowUtc.AddDays(-1),
            UpdatedAt = nowUtc.AddHours(-18)
        });

        db.Requirements.AddRange(
            new RequirementEntity
            {
                ProjectId = projectId,
                Code = "REQ-001",
                Text = "Display live substation load kW and voltage health cards with synthetic seed telemetry.",
                IntroducedInVersion = 1,
                Status = RequirementCoverageStatus.Done,
                LinkedIssueNumbersJson = "[1, 2]",
                LinkedAcceptanceTestsJson = "[\"REQ-001_dashboard_renders_substation_cards\"]"
            },
            new RequirementEntity
            {
                ProjectId = projectId,
                Code = "REQ-002",
                Text = "Expose GET /api/substations and GET /api/substations/{id}/metrics with OpenAPI 3.2 contract.",
                IntroducedInVersion = 1,
                Status = RequirementCoverageStatus.Done,
                LinkedIssueNumbersJson = "[1, 3]",
                LinkedAcceptanceTestsJson = "[\"REQ-002_get_substations_returns_synthetic_feed\"]"
            },
            new RequirementEntity
            {
                ProjectId = projectId,
                Code = "REQ-003",
                Text = "Allow operators to acknowledge voltage-sag alerts with role-gated audit trail.",
                IntroducedInVersion = 1,
                Status = RequirementCoverageStatus.TestsPassing,
                LinkedIssueNumbersJson = "[4, 5]",
                LinkedAcceptanceTestsJson = "[\"REQ-003_operator_acknowledges_voltage_alert\"]"
            },
            new RequirementEntity
            {
                ProjectId = projectId,
                FeatureId = featureId,
                Code = "REQ-004",
                Text = "Export 24-hour tariff comparison CSV and interactive chart.",
                IntroducedInVersion = 2,
                Status = RequirementCoverageStatus.TestsWritten,
                LinkedIssueNumbersJson = "[11, 12]",
                LinkedAcceptanceTestsJson = "[\"REQ-004_export_tariff_csv_downloads_24h_rows\"]"
            });

        // Work items across columns (Done, AwaitingApproval/InReview, Implementing/InProgress, Ready, Blocked, unmanaged card)
        var wi1 = new WorkItemEntity
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444401"),
            RepoId = repoId,
            IssueNumber = 1,
            IssueNodeId = "I_kwDO1",
            ProjectItemId = "PVTI_1",
            Title = "Walking skeleton: synthetic telemetry seed & health endpoint",
            BodyMarkdown = "Deliver initial walking skeleton with `/healthz` and synthetic substation seeder.\n\n### Acceptance Criteria\n- `GET /healthz` returns 200 OK\n- Synthetic telemetry seeder populates 5 substations",
            AcceptanceCriteriaJson = "[\"GET /healthz returns 200 OK\",\"Synthetic telemetry seeder populates 5 substations\"]",
            LabelsJson = "[\"factory\",\"walking-skeleton\"]",
            IssueAuthor = ownerLogin,
            WorkLabelAppliedBy = ownerLogin,
            PassesWorkLabelRule = true,
            State = WorkItemStateKind.Merged,
            BoardStatus = BoardStatus.Done,
            Priority = PriorityLevel.Critical,
            Size = ItemSize.Small,
            Attempts = 1,
            CurrentRound = 1,
            PrNumber = 101,
            PrUrl = $"https://github.com/{ownerLogin}/contoso-smart-grid/pull/101",
            HeadSha = "9a8b7c6d5e4f0101",
            CodingModel = "gemini-4-carbon",
            PredictedTouchPathsJson = "[\"src/Api/Program.cs\",\"src/Api/Seed/TelemetrySeeder.cs\"]",
            RequirementIdsJson = "[\"REQ-001\",\"REQ-002\"]",
            PmVerdict = "Ready",
            CreatedAt = nowUtc.AddDays(-4),
            ReadyAt = nowUtc.AddDays(-4),
            MergedAt = nowUtc.AddDays(-3).AddHours(-20)
        };

        var wi4 = new WorkItemEntity
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444404"),
            RepoId = repoId,
            IssueNumber = 4,
            IssueNodeId = "I_kwDO4",
            ProjectItemId = "PVTI_4",
            Title = "AT: Operator voltage-sag alert acknowledgement",
            BodyMarkdown = "Author executable Playwright & HTTP acceptance tests and `contract.md` for REQ-003.",
            AcceptanceCriteriaJson = "[\"Playwright test REQ-003_operator_acknowledges_voltage_alert fails on assertion\",\"contract.md specifies POST /api/alerts/{id}:ack\"]",
            LabelsJson = "[\"factory\",\"acceptance-test\"]",
            IssueAuthor = ownerLogin,
            WorkLabelAppliedBy = ownerLogin,
            PassesWorkLabelRule = true,
            IsAcceptanceTestIssue = true,
            GateProfile = GateProfile.AcceptanceTest,
            State = WorkItemStateKind.Merged,
            BoardStatus = BoardStatus.Done,
            Priority = PriorityLevel.High,
            Size = ItemSize.Small,
            Attempts = 1,
            CurrentRound = 1,
            PrNumber = 104,
            PrUrl = $"https://github.com/{ownerLogin}/contoso-smart-grid/pull/104",
            HeadSha = "9a8b7c6d5e4f0104",
            CodingModel = "gemini-4-carbon",
            PredictedTouchPathsJson = "[\"tests/acceptance/ISS-4/\"]",
            RequirementIdsJson = "[\"REQ-003\"]",
            PmVerdict = "Ready",
            CreatedAt = nowUtc.AddDays(-2),
            ReadyAt = nowUtc.AddDays(-2),
            MergedAt = nowUtc.AddDays(-2).AddHours(2)
        };

        var wi5Id = Guid.Parse("44444444-4444-4444-4444-444444444405");
        var wi5 = new WorkItemEntity
        {
            Id = wi5Id,
            RepoId = repoId,
            IssueNumber = 5,
            IssueNodeId = "I_kwDO5",
            ProjectItemId = "PVTI_5",
            Title = "Implement operator voltage-sag alert acknowledgement (REQ-003)",
            BodyMarkdown = "Implement `POST /api/alerts/{id}:ack` and the Fluent UI alert drawer per `tests/acceptance/ISS-4/contract.md`.\n\n### Acceptance Criteria\n- Operators can acknowledge open alerts with a note\n- Acknowledgement records audit event\n- Locked acceptance suite `ISS-4` passes",
            AcceptanceCriteriaJson = "[\"POST /api/alerts/{id}:ack persists acknowledgement\",\"Fluent alert drawer updates status live\",\"Acceptance suite ISS-4 passes\"]",
            LabelsJson = "[\"factory\",\"area:auth\"]",
            IssueAuthor = ownerLogin,
            WorkLabelAppliedBy = ownerLogin,
            PassesWorkLabelRule = true,
            GateProfile = GateProfile.Feature,
            State = WorkItemStateKind.AwaitingApproval,
            BoardStatus = BoardStatus.InReview,
            Priority = PriorityLevel.High,
            Size = ItemSize.Medium,
            Attempts = 1,
            CurrentRound = 2,
            MaxRounds = 3,
            PrNumber = 105,
            PrUrl = $"https://github.com/{ownerLogin}/contoso-smart-grid/pull/105",
            HeadSha = "7f8e9d0a1b2c0105",
            CodingModel = "gemini-4-carbon",
            OrchestrationInstanceId = "workitem-contoso-5-v1",
            BlockedByIssueNumbersJson = "[4]",
            PredictedTouchPathsJson = "[\"src/Api/Alerts/AlertEndpoints.cs\",\"web/src/AlertsDrawer.tsx\"]",
            RequirementIdsJson = "[\"REQ-003\"]",
            PmVerdict = "Ready",
            PreviewUrl = "https://pr-105-contoso-smart-grid.swedencentral.azurecontainerapps.io",
            CreatedAt = nowUtc.AddDays(-1).AddHours(-8),
            ReadyAt = nowUtc.AddDays(-1).AddHours(-6),
            UpdatedAt = nowUtc.AddMinutes(-15)
        };

        var wi11 = new WorkItemEntity
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444411"),
            RepoId = repoId,
            FeatureId = featureId,
            FeatureSlug = "tariff-export",
            IssueNumber = 11,
            IssueNodeId = "I_kwDO11",
            ProjectItemId = "PVTI_11",
            Title = "AT: 24-hour tariff comparison CSV export",
            BodyMarkdown = "Write acceptance tests and `contract.md` for `REQ-004` (`GET /api/tariffs/export.csv`).",
            AcceptanceCriteriaJson = "[\"Acceptance test REQ-004_export_tariff_csv_downloads_24h_rows fails on assertion\"]",
            LabelsJson = "[\"factory\",\"feature:tariff-export\",\"acceptance-test\"]",
            IssueAuthor = ownerLogin,
            WorkLabelAppliedBy = ownerLogin,
            PassesWorkLabelRule = true,
            IsAcceptanceTestIssue = true,
            GateProfile = GateProfile.AcceptanceTest,
            State = WorkItemStateKind.Implementing,
            BoardStatus = BoardStatus.InProgress,
            Priority = PriorityLevel.Medium,
            Size = ItemSize.Small,
            Attempts = 1,
            CurrentRound = 1,
            CodingModel = "gemini-4-carbon",
            OrchestrationInstanceId = "workitem-contoso-11-v1",
            PredictedTouchPathsJson = "[\"tests/acceptance/ISS-11/\"]",
            RequirementIdsJson = "[\"REQ-004\"]",
            PmVerdict = "Ready",
            SpecVersionPlanned = 2,
            CreatedAt = nowUtc.AddHours(-16),
            ReadyAt = nowUtc.AddHours(-15),
            UpdatedAt = nowUtc.AddMinutes(-4)
        };

        var wi12 = new WorkItemEntity
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444412"),
            RepoId = repoId,
            FeatureId = featureId,
            FeatureSlug = "tariff-export",
            IssueNumber = 12,
            IssueNodeId = "I_kwDO12",
            ProjectItemId = "PVTI_12",
            Title = "Implement 24-hour tariff CSV export & chart (REQ-004)",
            BodyMarkdown = "Implement `GET /api/tariffs/export.csv` and the Fluent tariff comparison chart.",
            AcceptanceCriteriaJson = "[\"CSV endpoint streams 24 hourly rows\",\"Tariff chart renders with Fluent tokens\"]",
            LabelsJson = "[\"factory\",\"feature:tariff-export\"]",
            IssueAuthor = ownerLogin,
            WorkLabelAppliedBy = ownerLogin,
            PassesWorkLabelRule = true,
            GateProfile = GateProfile.Feature,
            State = WorkItemStateKind.Queued,
            BoardStatus = BoardStatus.Ready,
            Priority = PriorityLevel.Medium,
            Size = ItemSize.Medium,
            CodingModel = "gemini-4-carbon",
            BlockedByIssueNumbersJson = "[11]",
            PredictedTouchPathsJson = "[\"src/Api/Tariffs/TariffEndpoints.cs\",\"web/src/TariffChart.tsx\"]",
            RequirementIdsJson = "[\"REQ-004\"]",
            PmVerdict = "Ready",
            SpecVersionPlanned = 2,
            CreatedAt = nowUtc.AddHours(-16),
            ReadyAt = nowUtc.AddHours(-15)
        };

        var wi13 = new WorkItemEntity
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444413"),
            RepoId = repoId,
            IssueNumber = 13,
            IssueNodeId = "I_kwDO13",
            ProjectItemId = "PVTI_13",
            Title = "Add substation anomaly threshold slider",
            BodyMarkdown = "Allow operators to tune voltage sag threshold in UI.",
            AcceptanceCriteriaJson = "[]",
            LabelsJson = "[\"factory\"]",
            IssueAuthor = ownerLogin,
            WorkLabelAppliedBy = ownerLogin,
            PassesWorkLabelRule = true,
            State = WorkItemStateKind.NeedsClarification,
            BoardStatus = BoardStatus.Blocked,
            Priority = PriorityLevel.Low,
            Size = ItemSize.Small,
            CodingModel = "gemini-4-carbon",
            PmVerdict = "NeedsClarification",
            BlockedReason = "Missing testable min/max kV bounds and persistence scope.",
            CreatedAt = nowUtc.AddHours(-10)
        };

        db.WorkItems.AddRange(wi1, wi4, wi5, wi11, wi12, wi13);

        // Sessions, Review Rounds, Findings, Gate Evaluation, Approval, Preview, Inbox Items, Audit Events
        db.AgentSessions.AddRange(
            new AgentSessionEntity
            {
                WorkItemId = wi5Id,
                RepoId = repoId,
                Mode = AgentSessionMode.Implement,
                CopilotTaskId = "task_cp_9001",
                Model = "gemini-4-carbon",
                State = CopilotTaskState.Completed,
                SessionUrl = $"https://github.com/{ownerLogin}/contoso-smart-grid/pull/105/agent-sessions/task_cp_9001",
                CommitShasJson = "[\"3a1b2c3d\"]",
                TokensUsed = 38400,
                EstimatedCostUsd = 0.42m,
                StartedAt = nowUtc.AddHours(-6),
                EndedAt = nowUtc.AddHours(-5).AddMinutes(-40)
            },
            new AgentSessionEntity
            {
                WorkItemId = wi5Id,
                RepoId = repoId,
                Mode = AgentSessionMode.Fix,
                CopilotTaskId = "task_cp_9002",
                TriggerCommentId = 880012,
                TriggerSummary = "@copilot address F-8d2a [Medium] src/Api/Alerts/AlertEndpoints.cs:48",
                Model = "gemini-4-carbon",
                State = CopilotTaskState.Completed,
                SessionUrl = $"https://github.com/{ownerLogin}/contoso-smart-grid/pull/105/agent-sessions/task_cp_9002",
                CommitShasJson = "[\"7f8e9d0a1b2c0105\"]",
                TokensUsed = 19200,
                EstimatedCostUsd = 0.21m,
                StartedAt = nowUtc.AddHours(-2),
                EndedAt = nowUtc.AddHours(-1).AddMinutes(-48)
            },
            new AgentSessionEntity
            {
                WorkItemId = wi11.Id,
                RepoId = repoId,
                Mode = AgentSessionMode.Implement,
                CopilotTaskId = "task_cp_9003",
                Model = "gemini-4-carbon",
                State = CopilotTaskState.InProgress,
                SessionUrl = $"https://github.com/{ownerLogin}/contoso-smart-grid/issues/11",
                CommitShasJson = "[]",
                TokensUsed = 9100,
                EstimatedCostUsd = 0.11m,
                StartedAt = nowUtc.AddMinutes(-8)
            });

        var round1Id = Guid.NewGuid();
        var round2Id = Guid.NewGuid();
        db.ReviewRounds.AddRange(
            new ReviewRoundEntity { Id = round1Id, WorkItemId = wi5Id, Round = 1, HeadSha = "3a1b2c3d", CopilotReviewId = 5001, CreatedAt = nowUtc.AddHours(-5) },
            new ReviewRoundEntity { Id = round2Id, WorkItemId = wi5Id, Round = 2, HeadSha = "7f8e9d0a1b2c0105", CopilotReviewId = 5002, CreatedAt = nowUtc.AddHours(-1) });

        db.Findings.Add(new FindingEntity
        {
            WorkItemId = wi5Id,
            ReviewRoundId = round2Id,
            ShortId = "F-8d2a",
            Fingerprint = "8d2a4b9c1e0f7732a11c9081b223e445f6677889",
            Severity = FindingSeverity.Medium,
            Path = "src/Api/Alerts/AlertEndpoints.cs",
            Line = 48,
            RuleHint = "input-validation",
            Body = "[Medium] Validate acknowledgement note length <= 500 chars before persisting.",
            Status = FindingStatus.Addressed,
            PmTriageClassification = "actionable",
            PmTriageReason = "Unbounded note string could violate SQL column max length.",
            FirstSeenRound = 1,
            LastSeenRound = 1
        });

        var gateConditionsSample = Enumerable.Range(1, 15).Select(n => new GateConditionEvaluation(
            n,
            n switch
            {
                1 => "Head SHA match",
                2 => "Required CI checks (source-pinned)",
                3 => "Copilot code review present",
                4 => "Blocking review findings",
                5 => "No contested High findings",
                6 => "Protected paths untouched",
                7 => "Diff size within limit",
                8 => "Spec conformance (model check)",
                9 => "Code scanning clean",
                10 => "Dependency review & policy",
                11 => "Acceptance tests first",
                12 => "Trusted preview deployment",
                13 => "Line not stopped",
                14 => "Operator approval & autonomy gate",
                _ => "Trusted PR origin & work label"
            },
            true,
            Passed: n != 14,
            IsPending: false,
            Summary: n == 14
                ? "Waiting for operator approval in UI (L1Risk: area:auth) on SHA 7f8e9d0."
                : "Verified green on head SHA 7f8e9d0.")).ToList();

        db.GateEvaluations.Add(new GateEvaluationEntity
        {
            WorkItemId = wi5Id,
            HeadSha = "7f8e9d0a1b2c0105",
            Profile = GateProfile.Feature,
            Passed = false,
            OutcomeKind = "OnlyApprovalMissing",
            ConditionsJson = JsonSerializer.Serialize(gateConditionsSample),
            ReasonsJson = "[\"[C14] Waiting for operator approval in UI (L1Risk) on SHA 7f8e9d0.\"]",
            EvaluatedAt = nowUtc.AddMinutes(-15)
        });

        db.Approvals.Add(new ApprovalEntity
        {
            WorkItemId = wi5Id,
            RepoId = repoId,
            HeadSha = "7f8e9d0a1b2c0105",
            Kind = ApprovalKind.L1Risk,
            Decision = ApprovalDecision.Pending,
            Summary = "PR #105 carries risk label 'area:auth' at autonomy L1. All 14 deterministic & CI/AT checks are green.",
            RequestedAt = nowUtc.AddMinutes(-15)
        });

        db.PreviewEnvironments.Add(new PreviewEnvironmentEntity
        {
            RepoId = repoId,
            PrNumber = 105,
            HeadSha = "7f8e9d0a1b2c0105",
            AppName = "pr-105-contoso-smart-grid",
            Url = "https://pr-105-contoso-smart-grid.swedencentral.azurecontainerapps.io",
            ImageDigest = "sha256:4e91c88b7f00112233445566778899aabbccddeeff00112233445566778899aa",
            State = PreviewState.Running,
            CreatedAt = nowUtc.AddMinutes(-28)
        });

        db.InboxItems.AddRange(
            new InboxItemEntity
            {
                RepoId = repoId,
                ProjectId = projectId,
                IssueNumber = 8,
                Title = "Could we export hourly tariff data as CSV?",
                Body = "During customer demos people ask if the 24h tariff table can be downloaded as CSV.",
                Author = "grid-enthusiast",
                ReactionsCount = 6,
                LabelsJson = "[\"enhancement\"]",
                State = InboxItemState.Planned,
                FeatureId = featureId,
                SuggestedGroup = "Tariff Export & Reporting",
                HtmlUrl = $"https://github.com/{ownerLogin}/contoso-smart-grid/issues/8",
                CreatedAt = nowUtc.AddDays(-2)
            },
            new InboxItemEntity
            {
                RepoId = repoId,
                ProjectId = projectId,
                IssueNumber = 14,
                Title = "Dark mode contrast on voltage sparkline tooltip",
                Body = "Tooltip text is slightly dim on OLED dark theme when hovering substation 3.",
                Author = "ux-reviewer-ext",
                ReactionsCount = 4,
                LabelsJson = "[\"ui\",\"accessibility\"]",
                State = InboxItemState.New,
                SuggestedGroup = "Accessibility & Chart Polish",
                HtmlUrl = $"https://github.com/{ownerLogin}/contoso-smart-grid/issues/14",
                CreatedAt = nowUtc.AddHours(-9)
            },
            new InboxItemEntity
            {
                RepoId = repoId,
                ProjectId = projectId,
                IssueNumber = 15,
                Title = "Keyboard focus ring on sparkline legend items",
                Body = "Tab navigation skips legend toggles on the substation sparkline card.",
                Author = "a11y-community",
                ReactionsCount = 3,
                LabelsJson = "[\"accessibility\"]",
                OwnerAttemptedDirectWorkLabel = true,
                State = InboxItemState.New,
                SuggestedGroup = "Accessibility & Chart Polish",
                HtmlUrl = $"https://github.com/{ownerLogin}/contoso-smart-grid/issues/15",
                CreatedAt = nowUtc.AddHours(-5)
            });

        db.AuditEvents.AddRange(
            new AuditEventEntity
            {
                Actor = ownerLogin,
                Action = "ProjectPlanApproved",
                EntityType = "Project",
                EntityId = projectId.ToString(),
                AfterJson = "{\"specVersion\":1,\"autonomy\":\"L1\"}",
                Timestamp = nowUtc.AddDays(-4)
            },
            new AuditEventEntity
            {
                Actor = "factory-orchestrator",
                Action = "IssueAssignedToCopilot",
                EntityType = "WorkItem",
                EntityId = wi5Id.ToString(),
                AfterJson = "{\"agent\":\"copilot-swe-agent[bot]\",\"customAgent\":\"factory-coder\",\"model\":\"gemini-4-carbon\"}",
                IssueNumber = 5,
                SessionId = "task_cp_9001",
                Timestamp = nowUtc.AddHours(-6)
            },
            new AuditEventEntity
            {
                Actor = "factory-orchestrator",
                Action = "GateEvaluated",
                EntityType = "WorkItem",
                EntityId = wi5Id.ToString(),
                AfterJson = "{\"outcome\":\"OnlyApprovalMissing\",\"missing\":[\"L1Risk\"]}",
                IssueNumber = 5,
                PrNumber = 105,
                Sha = "7f8e9d0a1b2c0105",
                Timestamp = nowUtc.AddMinutes(-15)
            });

        await db.SaveChangesAsync(ct);
    }
}
