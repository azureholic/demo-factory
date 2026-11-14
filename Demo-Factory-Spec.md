# Demo-Factory: Technical Specification

*An agent-driven GitHub software factory for building, and showcasing, demos.*

**Approach:** a custom orchestration runtime that reuses GitHub-native capabilities.
**Status:** Draft v0.8 (adds the work label and the issue inbox; v0.7 added feature specs for existing projects) · **Date:** 14 November 2026 · **Owner:** Remco Brosky

---

## 1. Summary

**Demo-Factory** ("the factory" in this document) builds demo applications on GitHub with AI agents. You give it a project name and a spec in its UI. It then:

1. creates a public GitHub repository with CI, security scanning, a project board and a backlog of issues;
2. hands each issue to the **Copilot cloud agent, which runs inside GitHub**;
3. has every PR reviewed by GitHub Copilot code review, and loops until CI and the review gate are green;
4. auto-merges, moves the board item, and picks the next issue based on its dependencies;
5. keeps `main` green, deploys a preview for every PR and a demo app from `main`.

Later, you can add a feature spec to a running project. The factory turns it into new backlog items without repeating the setup (§5.5).

An issue reaches the coding agent only when it carries the factory's **work label**. All other issues, including ones reported by other people, are ignored by the factory and kept as input that you can turn into a feature (§5.6).

**Design principle:** *agents decide and write code; deterministic code enforces permissions, transitions and quality gates.* A model never approves a merge, decides whether a dependency is satisfied, or sets an item's state. A model check can only **block** a step and escalate it to you; it is never the reason a step passes.

**Account model.** The factory runs against **one personal GitHub account** (no organization) with a Copilot subscription. The user signs in to the factory UI with GitHub, and the factory acts **on the user's behalf** with a GitHub App user access token. Repos, projects and issues are owned by the user.

**Execution split.** The coding agent, Copilot code review and CI all run in GitHub. The factory (Azure Container Apps) only orchestrates, schedules, gates and reports. It never clones, builds or runs target-repo code. Preview and demo apps run in a separate Azure resource group (§6.4).

| Concern | Owner |
|---|---|
| Writing code, fixing findings, fixing CI | **Copilot cloud agent, running in GitHub** (model configurable in the factory UI; default Gemini 4 Carbon) |
| Spec quality, prioritisation hints, decomposition proposals, finding triage | Project Manager Agent (Microsoft Agent Framework on a **Foundry** model, runs in the factory container) |
| State machine, scheduling, dependency resolution, gates, retries | Factory Orchestrator (deterministic .NET code on Durable Task) |
| Independent code review | GitHub Copilot code review (native, a ruleset setting that applies to every PR) |
| Build, test, lint | GitHub Actions in the target repo (native) |
| Merge | GitHub auto-merge, merges serialised by the factory (native) |
| Board | GitHub Projects (native) |

---

## 2. Goals and non-goals

### Goals
1. **G1:** Run unattended from "Ready" to "Done" for well-specified issues.
2. **G2:** Every PR is reviewed by Copilot, and the coding agent addresses the review findings.
3. **G3:** A PR merges only when all required checks pass on the latest commit and the review gate is green.
4. **G4:** Board status always reflects the true factory state, with no manual card moves.
5. **G5:** Work selection respects issue dependencies ("blocked by") and priority.
6. **G6:** Full auditability: every decision traces to an issue, run, commit, review and gate evaluation.
7. **G7:** A polished operator UI with live progress, dark and light mode, and a dependency graph.
8. **G8:** The coding model can be configured per repo, and per issue, from the UI, without a redeploy.
9. **G9:** A new project starts from a name and a spec in the UI; the factory creates the repo, board and backlog.
10. **G10:** `main` stays green: acceptance tests are written first and locked, and a red `main` stops the line.
11. **G11:** The spec stays the source of truth, and every requirement is traceable to its issues, tests and PRs.
12. **G12:** Every PR has a preview, and every green `main` updates a demo environment.
13. **G13:** A feature spec can be added to an existing project from the UI. It produces new backlog items and repeats nothing from bootstrap.
14. **G14:** Only issues with the work label are built. Other issues, including ones reported by other people, are input for new features.

### Non-goals (v1)
- Deciding intent. You own the spec. The PM agent proposes plans and changes, and nothing is applied until you approve it.
- Production deployments and releases. The factory deploys previews and a demo environment only.
- Replacing GitHub UI features that already work (PR view, diff, checks).

---

## 3. Technology decisions

### 3.1 Server side
- **.NET 11 / C# 15.** .NET 11 went GA on 10 November 2026. It is an STS release supported until 14 November 2028. C# 15 union types fit the state machine and gate results well.
- **ASP.NET Core 11** (Minimal APIs, SignalR) for the API, webhooks and the realtime hub.
- **Aspire AppHost** for local development (all services, emulators and dashboards with one command), deployed to Azure Container Apps with `azd`.
- **EF Core 11** on **Azure SQL Database** for the read model and audit log.
- **Octokit.NET** for GitHub REST, plus a thin typed GraphQL client for Projects v2, issue dependencies and auto-merge.

### 3.2 Agent harness

The factory hosts only the **Project Manager agent** and the orchestration logic. Coding runs in GitHub.

- **Harness:** Microsoft Agent Framework (`Microsoft.Agents.AI`), with agents created from a **Foundry** project (`AIProjectClient(...).AsAIAgent(model: <deployment>, instructions: ...)`) and authenticated with managed identity.
- **No coding runtime in the factory.** There is no local coding loop, no shell or file tools, and no repository code inside the container, so the GitHub Copilot SDK is not used.
- **Durability:** the Durable Task Scheduler with the Agent Framework Durable Extension, in *bring-your-own-compute* mode on Container Apps. Orchestrations are checkpointed, so a restart never loses work or repeats a completed step.
- **Orchestration versioning.** Orchestrations replay their history, so a code change can break instances that are in flight. Every orchestration carries a version. A new deployment starts new instances on the new version and lets running ones finish on the old code path; the eternal `ProjectScheduler` switches version at its next `ContinueAsNew`. Replay tests in CI run recorded histories against the new code.
- **Structured outputs only.** Every PM agent call returns JSON that is validated against a schema. If validation fails, the call is retried once, then a deterministic default is used.
- **Model choice:** the Foundry deployment is set per environment (and can be overridden per repo) in the UI. It is kept in an EU region for data residency.

### 3.3 Frontend
- **React 19 + TypeScript + Vite**
- **Fluent UI React v9** (`@fluentui/react-components`, `@fluentui/react-icons`), using the Fluent 2 design language
- TanStack Query (server state), TanStack Router or React Router (routing), `@microsoft/signalr` (live updates)
- `@xyflow/react` (React Flow) for the dependency graph, with nodes styled from Fluent tokens
- Charts: Fluent UI React Charts (v9) where the needed chart types exist, otherwise Recharts themed with Fluent tokens
- "Sign in with GitHub" handled by the API (backend-for-frontend pattern): the browser only holds an HTTP-only session cookie, and GitHub tokens never reach the browser

### 3.4 Azure hosting

| Component | Azure service | Notes |
|---|---|---|
| `factory-web` | Container App (nginx serving the SPA) | Public ingress, behind Front Door optional |
| `factory-api` | Container App | Webhooks, REST API for the UI, SignalR hub |
| `factory-orchestrator` | Container App (worker, no ingress) | Durable Task worker. Scales on scheduler backlog. |
| Workflow state | Durable Task Scheduler | Managed, checkpointed orchestrations, dashboard |
| Events | Azure Service Bus | Webhook fan-in (reference profile only) |
| Read model and audit log | Azure SQL Database (serverless) | EF Core 11 |
| Evidence | Azure Blob Storage | Gate evidence snapshots, PM agent outputs |
| Secrets | Azure Key Vault | GitHub App key held as a **non-exportable key** (JWTs signed via Key Vault); the signed-in user's GitHub refresh token (§10.1), encrypted with a Key Vault key |
| Realtime | Azure SignalR Service | Needed when the API scales out (reference profile only) |
| Images | Azure Container Registry | |
| Model | Microsoft Foundry (EU region) | PM agent and triage models |
| Observability | Azure Monitor and Application Insights (OpenTelemetry) | Agent Framework emits OpenTelemetry |
| Identity | Managed identities for Azure resources; GitHub sign-in for the UI | No connection strings in config |

### 3.5 Deployment profiles

The table above is the **reference profile**. For a single user, the **lean profile** is the default.

| Aspect | Lean profile (default) | Reference profile |
|---|---|---|
| Containers | One container app, single replica: API, orchestrator worker and the static web app in one process | Three container apps, scaled independently |
| Realtime | In-process SignalR | Azure SignalR Service |
| Webhook intake | The API writes each delivery to a SQL inbox table (de-duplicated on the delivery ID) and raises the Durable event directly | Service Bus between the API and the orchestrator |
| Networking | Public endpoints, Entra-only authentication with managed identities, firewall rules limited to the container app | VNet integration, and private endpoints for SQL, Key Vault, Service Bus, Storage and Foundry |

Both profiles run the same code and use Durable Task Scheduler, Azure SQL, Key Vault, Blob Storage, Foundry and Application Insights. The profile is an `azd` environment setting. In both, outbound traffic is limited to GitHub and Azure, and no target-repo code runs in the factory's own containers.

---

## 4. Architecture

```
┌────────────────────────────────────────── GitHub ───────────────────────────────────────────┐
│  Target repo: issues, rulesets, Actions CI                                                  │
│  Copilot cloud agent ──► draft PR ──► ready ──► Copilot code review (ruleset, every push)   │
│        ▲  @copilot fix comments                       │                                     │
│  GitHub Project v2 board                     auto-merge (serialised)                        │
└────────┼───────────────────────────────────────────────┼────────────────────────────────────┘
         │ your delegated token                          │ webhooks
         │ (all GitHub writes, as you)                   ▼
         │                 ┌──────────────────┐  Service Bus  ┌─────────────────────────┐
         │   ┌──────────┐  │   factory-api    │──────────────►│  factory-orchestrator   │
         │   │factory-web│◄►│ webhooks, REST,  │◄──────────────│  Durable Task worker    │
         │   │React+Fluent│ │ SignalR hub      │               │  - ProjectScheduler     │
         │   └──────────┘  └────────┬─────────┘               │  - WorkItemOrchestration│
         │                          │                         │  - Gate evaluator       │
         └──────────────────────────┼─────────────────────────│  - PM agent (MAF+Foundry)│
                                Azure SQL                     └─────────────────────────┘
                           (read model, audit)       App token: webhooks and the gate check run only
```

The diagram shows the reference profile. In the lean profile (§3.5) a SQL inbox table replaces Service Bus, and the three factory boxes are one container app.

### 4.1 Event flow
1. GitHub sends a webhook to `factory-api`. The API checks the HMAC signature, de-duplicates on `X-GitHub-Delivery`, and hands the event to the orchestrator (through the SQL inbox in the lean profile, or Service Bus in the reference profile).
2. The orchestrator turns each event into a **Durable external event** (`PrOpened`, `PrReadyForReview`, `CopilotWorkStarted`, `CopilotWorkFinished`, `ChecksCompleted`, `ReviewSubmitted`, `PrMerged`, `IssueClosed`, `DependencyChanged`, and so on) on the correct orchestration instance. Events caused by actors outside the trust boundary (§11.4) are recorded and ignored.
3. A **reconciler** (timer orchestration, every 10 minutes) compares GitHub with factory state and repairs drift caused by missed webhooks. It also polls the agent tasks API for Copilot session state (`queued`, `in_progress`, `completed`, `failed`, `idle`, `waiting_for_user`, `timed_out`, `cancelled`).
4. A **board poller** covers the project board. GitHub sends `projects_v2_item` webhooks only for organization-level projects, not for projects owned by a personal account. The poller reads each project's items every 60 seconds (one paged GraphQL query per project) and raises `StatusChanged` when a card's Status differs from the last snapshot. The UI's "Mark ready" action raises the same event immediately.

---

## 5. Projects: bootstrap and adding features

This section covers the two ways scope enters the factory: a new project (§5.1 to §5.4) and a feature added to an existing project (§5.5).

A project starts in the factory UI with a **name and a spec**. The factory turns that into a governed GitHub repository with a backlog, then starts delegating. Bootstrap is a deterministic Durable orchestration (`ProjectBootstrapOrchestration`). Every step is idempotent and resumable, and every step is shown live in the UI.

### 5.1 Flow

| # | Step | Who | Output |
|---|---|---|---|
| 1 | **Intake** in the "New project" wizard: name, spec (Markdown, or uploaded documents), stack template, autonomy level, coding model, deployment target (default: Azure Container Apps previews and a demo environment). Visibility is **public** by default. | Human | Project draft |
| 2 | **Spec analysis**: the PM agent checks the spec against the Definition of Ready and asks clarifying questions in the wizard. The human answers or skips. It also runs a **public-safe check** that flags customer names, internal URLs, credentials and personal data, because the spec is committed to a public repo. | PM agent | Clarified, public-safe spec with requirement IDs |
| 3 | **Plan generation**: architecture outline (ADR-0001), milestones, epics, issues with acceptance criteria, size, priority and `blocked by` dependencies, plus a rough cost and duration forecast | PM agent | Plan (JSON, schema-validated) |
| 4 | **Plan review**: the human reviews the backlog as a list and as a dependency graph, then edits, splits, merges or deletes items and approves. *This is the one recommended human checkpoint.* | Human | Approved plan |
| 5 | **Create repo** from the selected golden-path template repository (§5.2), with the spec committed as `docs/spec.md` and the plan as `docs/adr/0001-architecture.md` | Your delegated token | Repo with green `main` |
| 6 | **Apply security baseline** (§5.3): per-repo security settings via the REST API, including push protection and CodeQL default setup | Your delegated token | Security features on |
| 7 | **Apply rulesets and settings** (§10.2): rulesets with pinned check sources, Actions defaults and Copilot cloud agent settings | Your delegated token | Governed repo |
| 8 | **Create the board**: `copyProjectV2` from your own template project (fields, views, built-in workflows; created once in the setup wizard), linked to the repo. The project's auto-add workflow stays off. | Your delegated token | Project v2 (user-owned) |
| 9 | **Create the backlog**: labels (including the work label, §5.6), milestones, epics as parent issues with sub-issues, `addBlockedBy` dependencies, project items with fields set. Writes are paced (about one per second, honouring `Retry-After`) to stay under GitHub's secondary rate limits. | Your delegated token | Issues on the board |
| 9b | **Azure wiring**: create (or reuse) the shared previews Container Apps environment, the per-repo managed identity with federated credentials (`preview` and `demo` environments), and GitHub environments `preview` and `demo`, both with deployment branches restricted to `main` | Factory (managed identity) + your delegated token | Previews ready |
| 10 | **Smoke test**: CI green on `main`, first CodeQL analysis done, readiness checklist all green, the model in the catalogue is enabled | Orchestrator | Readiness report |
| 11 | **Start**: register the repo and start the `ProjectScheduler`. The walking-skeleton issue goes first. | Orchestrator | Factory running |

If a step fails, bootstrap pauses with the reason in the UI ("Retry step" or "Abort and clean up"). Abort deletes only what this bootstrap created. Repo deletion needs an extra Admin confirmation.

### 5.2 Golden-path templates

Templates are **GitHub template repositories** in your account (for example `demo-factory-template-dotnet-react`). They are versioned with tags, and there is one per stack, for example "`.NET 11 API + React/Fluent`" or "`Python FastAPI`". Each template contains:

| Area | Files |
|---|---|
| CI | `.github/workflows/ci.yml`: build, lint, unit tests, coverage; job names match the ruleset's required checks. It declares `permissions: contents: read`, builds the container image and uploads it as an artifact. It never pushes to a registry. |
| Dependencies | `.github/dependabot.yml` (app ecosystems **and** `github-actions`, grouped weekly updates); `.github/workflows/dependency-review.yml`. Dependabot auto-merge is handled by the factory (§5.4), not by a workflow. |
| Security | Code scanning default setup (no workflow file); an advanced `codeql.yml` only when the stack needs a custom build or custom queries. `SECURITY.md`. |
| Agents | `.github/copilot-instructions.md`, `AGENTS.md`, `.github/agents/factory-coder.md`, `.github/workflows/copilot-setup-steps.yml` |
| Quality | `scripts/quality` (one entry point for build, lint and test; a protected path), `.editorconfig`, linter configs, PR template, `CODEOWNERS` (humans own protected paths) |
| Docs | `README.md`, `docs/spec.md`, `docs/adr/` |
| Issue intake | `.github/ISSUE_TEMPLATE/` forms for bug reports and ideas from other people. They never apply the work label (§5.6). |
| Acceptance | `tests/acceptance/_harness/` (runner, configuration, fixtures and the `pending.json` logic; template-owned and protected), `tests/acceptance/pending.json` |
| Deploy | `preview.yml` (`workflow_run`; a deploy job and a separate test job without credentials, §6.4), `deploy-demo.yml` (on `main`); Azure OIDC federated credentials, no stored secrets; `infra/` (Bicep, azd) for the previews and demo apps. There is no teardown workflow: the factory deletes previews. |

**Template drift:** when a template version changes, the factory opens an "upgrade template" PR on every repo created from it. These PRs change protected paths, so they use the `template-upgrade` gate profile: they merge only after you approve them in the factory UI (gate condition 14).

### 5.3 Security baseline

Demo-Factory repos are **public by default**. On a personal account this gives the full GitHub security baseline at no extra cost. The factory applies it per repo through the REST API (`security_and_analysis`, the Dependabot endpoints and the code scanning default-setup endpoint):

| Control | Setting |
|---|---|
| Dependency graph, Dependabot alerts, security updates, version updates | On |
| Secret scanning with **push protection** | On. Pushes containing supported secrets are blocked, for you and for Copilot alike. |
| Code scanning | CodeQL default setup |
| Merge protection | Ruleset rule **"Require code scanning results"** (CodeQL; blocks on High or higher security alerts and on errors) |
| Dependency review | `dependency-review.yml` on every PR (vulnerabilities and licence allowlist) |
| Private vulnerability reporting | On |
| Actions | Standard GitHub-hosted runners are free for public repos. Actions are pinned to a full commit SHA; Dependabot keeps them current. |

Ruleset merge protection doesn't apply to Dependabot PRs analysed by default setup, so the factory gate also checks code scanning on the head SHA (§8.3, condition 9).

**Public-repo hygiene:**
- The public-safe check runs at intake, on every added feature (§5.5) and on every spec change (§6.3).
- Demo data is synthetic, generated by the template's seeders.
- Preview and demo environments hold no secrets (§6.4).
- Other people can report issues, but only issues with the work label, written by you or the factory, are built (§5.6, §11.4).

**Private repos (optional fallback):** these need GitHub Pro for rulesets and auto-merge. Secret Protection and Code Security can't be bought for a personal account, so the template switches to gitleaks and Semgrep CE in CI. Those are detective controls only, and they are documented in the wizard.

**Alert loop (agentic remediation):**
- **New CodeQL alerts on `main`:** the factory assigns them to `copilot-swe-agent[bot]` through the code scanning REST API, in batches of up to 25 per PR. The fix PR goes through the normal gate.
- **Secret detected:** the factory pauses the repo and notifies you. Rotation is always done by a human.
- **Dependabot alerts with no automatic fix:** the factory creates an "upgrade" issue for Copilot.

### 5.4 Dependabot policy

| Update type | Handling |
|---|---|
| Security update (any semver) | Auto-merge if the gate passes. Merged ahead of feature PRs. |
| Patch / minor, version update | The factory reads the update type from the Dependabot PR and enables auto-merge (squash) with your delegated token. The merge happens once the gate passes. No workflow with write access is involved. |
| Major | Labelled `deps:major`. The factory closes the Dependabot PR and creates an "Upgrade X from a to b" issue, with the changelog link and breaking-change notes, so Copilot does the migration through the normal flow. |
| `github-actions` ecosystem | Like patch/minor, but changes to workflow files are protected paths. Allowed automatically only for SHA-pin bumps of actions already on the allowlist. |

The `demo-factory/review-gate` check has a **Dependabot profile**:
- CI green
- Code scanning clean
- **Dependency review** clean (no new vulnerable dependencies; licences on the allowlist).
- Update type allowed by the policy
- No open High Copilot review findings

Dependabot PRs don't take a coding slot. Security updates merge ahead of feature PRs; version updates queue behind them.

### 5.5 Adding a feature to an existing project

You can add a **feature specification** to a project that is already running. The factory turns it into new backlog items. Nothing from bootstrap is repeated: no repo, settings, template, board or Azure changes.

This works for projects the factory created and for repos you onboarded later (§14.2, screen 6). The project must pass its readiness check and must not be paused.

| # | Step | Who | Output |
|---|---|---|---|
| 1 | **Intake** in the "Add feature" wizard: title, feature spec (Markdown or uploaded documents), and optionally priority, milestone and a coding-model override. You can also start from issues that other people reported (§5.6). | You | Feature draft |
| 2 | **Analysis.** The PM agent reads the feature spec against the project as it is now: the current spec and requirements, open and closed issues, ADRs and the repo's file tree (through the API, not a clone). It asks clarifying questions, runs the public-safe check, and flags overlaps or contradictions with existing requirements. | PM agent | Clarified feature spec |
| 3 | **Plan increment.** New requirement IDs that continue the project's sequence; a feature epic with issues and acceptance-test issues (§6.1); size and priority; `blocked by` links, including links to existing issues; an ADR if the feature changes the architecture; change-request issues where it alters finished work; a cost and duration forecast | PM agent | Plan increment (JSON, schema-validated) |
| 4 | **Review.** The plan-review screen opens in increment mode: new items are highlighted against the existing backlog and dependency graph. You edit, split, merge or delete items, choose where each goes (Ready or Backlog), and approve. | You | Approved increment |
| 5 | **Apply.** (a) The factory opens a docs-only PR with your delegated token. It adds `docs/features/<nnn>-<slug>.md` and a line in the feature index in `docs/spec.md`. It merges with the `spec` gate profile, covered by your approval in step 4, and tags a new spec version. (b) The factory creates the epic, issues, sub-issue links, dependencies and board items, with paced writes. (c) It raises `StatusChanged`. | Orchestrator | Issues on the board |
| 6 | **Delegate.** The `ProjectScheduler` ranks the new Ready items together with everything else (§8.1). No restart is needed. | Orchestrator | Work starts |

**Rules:**
- **Running work is not disturbed.** Items in progress keep going. If the increment affects one, the review screen flags it and you choose: let it finish and follow with a change request, or cancel it.
- **One apply at a time per project.** Several features can be in draft at once. Increments are applied one after another, so requirement numbers and spec versions stay consistent.
- **Stop the line still applies.** While `main` is red, the issues are created but nothing new starts (§6.2).
- **Resumable.** `FeatureIntakeOrchestration` is a Durable orchestration with idempotent steps. If a step fails, it pauses with "Retry step" or "Abort". Abort closes only the issues this intake created that have not started.
- **Withdrawing a feature.** You can withdraw a feature later. Issues that have not started are closed as not planned. For started or merged work, the PM agent proposes change requests and you decide.
- **Traceability.** Each feature has a slug. Its issues carry the label `feature:<slug>` and the board's Feature field, and its requirements link to it. The UI shows progress and requirement coverage per feature.
- **Small features are fine.** An increment can be a single issue. Whether it gets a separate acceptance-test issue follows the size rule in §6.1.

**How this relates to the living spec (§6.3).** Adding a feature is the route for new scope. It is stored as a change proposal of kind `feature`, and it is built before the spec PR merges, so one approval covers both the spec text and the backlog. To change or remove existing requirements, edit the spec; that produces a change proposal after the edit lands.

### 5.6 The work label and the issue inbox

**Only issues with the work label are built.** The label is `factory` by default (policy `workLabel`). An issue reaches the coding agent only if all of these hold:
- it carries the work label;
- the label was applied by you or by the factory. The factory checks who applied it, because issue forms and other automation can apply labels too;
- the issue was created by you or by the factory.

How the label gets there:
- **Automatically.** Everything the factory creates carries it: backlog items from bootstrap and from feature increments, acceptance-test issues, change requests, upgrade issues and "main is red" issues.
- **By you.** An issue you write yourself on GitHub joins the backlog when you add the label. The factory puts it on the board in Backlog, and it starts once you move it to Ready.
- **Removing it** takes the issue out of the factory. If work has not started, the issue is no longer scheduled. If work is in progress, it is cancelled (§7.1).

The factory locks the conversation on every issue and PR it manages, so only you and the agents can comment there. Other people's text therefore never sits in a thread that an agent reads.

**Issues without the label are ignored, and kept as input.** Anyone can report a bug or an idea on a public repo, and the template ships issue forms for that. Until you act on such an issue, the factory does not schedule it, add it to the board, comment on it or assign it. That holds even if someone other than you adds the label, and even if the issue is put on the board by hand. These issues appear in the project's **issue inbox** in the UI.

**From reported issues to a follow-up plan:**

| # | Step | Who |
|---|---|---|
| 1 | In the inbox, select one or more issues and choose "Create feature". The PM agent suggests groupings (related issues, likely duplicates) to help you pick. | You |
| 2 | The PM agent drafts a feature spec from the selected issues. It treats their text as untrusted data: it summarises what is asked for and never follows instructions found in it. | PM agent |
| 3 | You review and edit the draft. From here the flow is §5.5: analysis, plan increment, review, apply. | You |
| 4 | On apply, the factory creates the new backlog items, with the work label. Each source issue gets a comment that names the feature and its epic. The source issues stay unlabelled and are never built directly. | Orchestrator |
| 5 | When the feature's epic closes, the factory comments on each source issue and closes it (policy `intake.closeSourceIssuesWhenDone`, default on). | Orchestrator |

"Dismiss" hides an issue from the inbox. Nothing changes on GitHub.

**Why a reported issue is never built directly, even if you label it.** Its author can edit the text after you labelled it, and that text would go to the coding agent as instructions. Going through a feature means the agent only ever works from issues that the factory wrote and you approved. If you label someone else's issue anyway, the UI points you to "Create feature".

**Closing a repo to outside input.** Set `intake.acceptOutsideIssues: false`. The factory then sets GitHub's interaction limit to collaborators only, and renews it before it expires (six months at most).

## 6. Quality loops

Four mechanisms keep autonomous delivery honest and demo-ready. Each one is enforced deterministically by the orchestrator and shown in the UI.

### 6.1 Acceptance tests first

**Goal:** the coding agent can't make a feature "pass" by changing the definition of done.

1. **Planning.** For each feature issue, the PM agent also creates an **acceptance-test issue** (`AT: <feature>`), and makes the feature issue `blocked by` it. Each acceptance criterion maps to at least one named test, with the requirement ID in the test name (for example `REQ-012_user_can_reset_password`).
   - Items sized Small may skip the separate issue (policy `acceptanceTests.separateIssueMinSize`, default Medium). Their tests ship in the feature PR and are locked on merge. This halves the Copilot sessions for small work. The guarantee is weaker, because one session writes both the tests and the code.
2. **AT implementation.** Copilot writes two things under `tests/acceptance/<ISSUE-ID>/`:
   - **Executable tests:** Playwright for UI flows, HTTP-level tests for APIs.
   - **An interface contract** (`contract.md`, plus an OpenAPI fragment where there is an API): the routes, `data-testid` names and request and response shapes the tests rely on. Tests written before the feature exists would otherwise be guesses. The PM agent copies the contract into the feature issue's instructions.

   The issue ID is added to `tests/acceptance/pending.json`, so CI runs these tests in **report-only** mode and `main` stays green.
3. **AT gate profile.** An AT PR passes when:
   - the harness's own smoke test passes, which shows the runner and fixtures work;
   - every new test reports a **failure** (an assertion) and none reports an **error** (a crash, timeout or missing fixture) in the JUnit report. This is what "fails for the right reason" means, and the report decides it, not a model;
   - the PM agent's mapping check finds no acceptance criterion without a test. This is a model check: a miss blocks the PR and comes to you, and a pass is never enough alone.

   At autonomy L1, AT PRs also need your approval in the UI (one click, with criteria and tests side by side). AT PR tests run in ordinary CI, against the app started inside the runner.
4. **Locking.** Once an AT PR is merged, `tests/acceptance/<ISSUE-ID>/**` is a **protected path** for every later PR (gate condition 6). The shared harness (`tests/acceptance/_harness/**`) is template-owned and always protected.
5. **Feature gate profile.** A feature PR passes only when:
   - its acceptance suite passes against the PR's preview. The suite is taken from `main`, not from the PR, and is run by the trusted preview workflow (§6.4). Repos without a deploy target run it in CI; conditions 6 and 11 still guarantee that the tests and the runner are unchanged;
   - the files under `tests/acceptance/<ISSUE-ID>/` are byte-identical to `main`;
   - the only change to `pending.json` is the removal of its own ID (gate condition 11).
6. **Escape hatch.** If Copilot thinks an acceptance test is wrong, it says so in a PR comment tagged `AT-DEFECT`. Only a comment written by the Copilot agent counts. The factory then creates an `AT fix` issue (tests only, normal gate), and the feature stays blocked until it merges. There is a maximum of 2 AT fixes per feature; after that the item goes to `Blocked` for you.

### 6.2 Stop the line

**Trigger:** a required workflow fails on `main`, or the post-merge deploy or smoke test of the demo environment fails (§6.4).

1. **Halt.** The `ProjectScheduler` enters `LineStopped`:
   - no new assignments;
   - the gate returns `pending: line stopped` for every PR (Dependabot included), **except** PRs with the `revert` or `line-fix` profile, which exist to restore `main`;
   - Copilot sessions already running may finish.
2. **Flake check.** The failed jobs are re-run once. If they pass:
   - the run is recorded as **flaky**;
   - a P1 "Fix flaky test X" issue is created;
   - the line resumes.
3. **Revert.** If exactly one PR was merged since the last green `main` SHA:
   - the factory opens a **revert PR** (GraphQL `revertPullRequest`) with the `revert` gate profile (CI green; Copilot review findings are advisory; exempt from condition 13);
   - the original issue is reopened as Ready, with the failure logs attached and Attempts + 1.
4. **Fix forward.** If there are several suspects, or the revert doesn't turn `main` green:
   - a **P0 "Main is red"** issue goes to Copilot with logs and the commit range. Its PR uses the `line-fix` profile (the full feature gate, exempt from condition 13);
   - if it isn't fixed within 2 rounds, you're notified (UI, plus Teams if configured).
5. **Resume.** The first green `main` run clears `LineStopped`, and the scheduler continues.

**UI:** a red Andon banner across all screens, with the cause, suspect PRs, actions in progress and time stopped. **Metric:** mean time to restore (MTTR) on the dashboard.

### 6.3 Living spec

**Goal:** the spec stays the source of truth while the project evolves.

- **Requirement IDs.** At first plan, the PM agent gives every requirement in `docs/spec.md` a stable ID (`REQ-001` …). Issues, acceptance tests and PRs reference these IDs, which gives a full **traceability chain**.
- **Editing.** To add new scope, use "Add feature" (§5.5). To change or remove existing requirements, you edit the spec in the UI's spec editor (or directly on GitHub). The UI path opens a `spec-change` PR with your delegated token. The spec is a protected path for agents. A spec PR you confirm in the UI is merged with the `spec` gate profile: docs only, and the public-safe check raised no flags, or you dismissed each flag. The public-safe check is a model check, so it can hold a spec PR back but never clears one without you.
- **Spec delta.** When a new spec version lands on `main`, the PM agent compares it with the previous version and the current backlog, and produces a **change proposal**:
  - new issues and acceptance-test issues;
  - updated acceptance criteria for items not started yet;
  - **change-request issues** (`CR: …`) for work already done;
  - issues to close as obsolete;
  - dependency changes.
- **Review.** The change proposal opens in the plan-review screen as a diff (added, changed, removed). Items in progress that are affected are flagged; you either let them finish, followed by a CR, or cancel them. Nothing changes until you apply the proposal.
- **Versioning.** Each spec version is tagged (`spec-v3`), and every issue records the spec version it was planned against.
- **UI:** the spec editor shows **coverage per requirement**: planned, in progress, tests written, tests passing, done.

### 6.4 Preview environments

**Goal:** every PR can be clicked and shown, which suits demos, and acceptance tests run against a real deployment.

**Trust rule:** PR code runs only inside the preview container. No job that holds credentials ever checks out, builds or executes PR code.

- **Eligibility.** A PR gets a preview only if its head branch is in the repo itself and its author is trusted (§11.4): the Copilot agent, Dependabot or you. Fork PRs never get one.
- **Build (untrusted).** `ci.yml` runs on `pull_request` with a read-only token. It builds the container image and uploads it as a workflow **artifact**. It does not push to a registry.
- **Deploy (trusted).** `preview.yml` is triggered by **`workflow_run`** after CI succeeds. A `workflow_run` workflow always runs the definition on `main`, so a PR can't change it. Its run name carries the PR number and head SHA. The deploy job:
  - checks eligibility from the event payload;
  - downloads the artifact and treats it as data: load the image, push it to GHCR under `<repo>/preview`, record the digest;
  - signs in to Azure with **OIDC federated credentials**. The identity is a user-assigned managed identity with rights on the **previews resource group only**. The federated subject is the `preview` environment, and that environment's deployment branches are restricted to `main`. A workflow on a PR branch therefore can't obtain an Azure token;
  - deploys the image **by digest** as the app `pr-<n>-<repo>`: one replica at most, capped CPU and memory, scale to zero, no secrets, no managed identity on the app, synthetic seed data only.
- **Test (no credentials).** A second job has `permissions: {}`, no environment and no cache. It checks out `main`, runs the smoke suite and the feature's acceptance suite (§6.1) against the preview URL, and uploads the report.
- **Result.** The factory reads the conclusion of that run through the Actions API and records it for the PR's head SHA. It accepts a run only if the workflow file is `preview.yml`, the event is `workflow_run` and the ref is `main`. No commit status is used, because any workflow could write one. Gate condition 12 uses this result.
- **Show.** The factory comments the preview URL on the PR (updated on each push) and shows it on the board card and in the work-item detail.
- **Teardown.**
  - The factory deletes the app when it receives the PR-closed webhook, using its own managed identity.
  - A nightly sweeper in the factory deletes previews older than 72 hours.
  - There is no teardown workflow. It would need `pull_request_target`, which GitHub blocks by default in public repos.
  - Concurrency: at most WIP + 2 preview apps.
- **Demo environment.** Every green `main` deploys to a stable **demo** app (`demo-<repo>`) with a fixed URL for showcasing.
  - `deploy-demo.yml` builds the image and deploys it by digest in the same run, under the `demo` environment (also restricted to `main`) and a separate `<repo>/demo` package. An image tag overwritten from a PR branch is therefore never used.
  - Its smoke tests feed stop-the-line (§6.2).
  - A **"Showcase" link** in the UI opens the demo URL alongside the board and the spec coverage.

## 7. Domain model

### 7.1 Work-item state machine

| Factory state | Board Status | Entered when | Exits to |
|---|---|---|---|
| `Queued` | Ready | Status set to Ready by a human or PM | `SpecReview` |
| `SpecReview` | Ready | Scheduler selects the item | `NeedsClarification`, `Claimed` |
| `NeedsClarification` | Blocked | PM agent finds missing acceptance criteria or ambiguity | `Queued` (after the issue is edited) |
| `Claimed` | In Progress | Scheduler assigns a WIP slot | `Implementing` |
| `Implementing` | In Progress | Issue assigned to Copilot; draft PR in progress | `PrOpen`, `NeedsClarification` (session `waiting_for_user`), `Failed` |
| `PrOpen` | In Review | Copilot marks the PR ready for review; factory enables auto-merge | `Validating` |
| `Validating` | In Review | Waiting for CI and Copilot review on the head SHA | `Fixing`, `AwaitingApproval`, `AwaitingMerge`, `Blocked` |
| `Fixing` | In Review | Gate failed and round < max; PR set to draft and `@copilot` fix comment posted | `Validating` |
| `AwaitingApproval` | In Review | Every gate condition holds except your approval (condition 14) | `AwaitingMerge` (approved), `Fixing` (changes requested) |
| `AwaitingMerge` | In Review | Gate green; auto-merge pending | `Merged`, `Validating` (new push or conflict) |
| `Merged` | Done | PR merged, issue closed | terminal |
| `Blocked` | Blocked | Max rounds, contested High finding, dependency cycle, policy violation | `Queued` (by human) |
| `Failed` | Blocked | Infrastructure or Copilot session failure after retries | `Queued` |
| `Cancelled` | Backlog | You cancel the item, or the work label is removed (§5.6) | terminal |

States are modelled as a C# 15 closed hierarchy, so the compiler checks every transition handler.

### 7.2 Entities (Azure SQL read model)
- **Project:** id, name, spec (versioned), templateId and version, plan (JSON), planApprovedBy and planApprovedAt, bootstrapInstanceId, state
- **Repository:** id, projectId, owner/name, installationId, GitHub project ID, policy reference, paused flag
- **RuntimeSettings:** scope (global, repo or work item), codingModel, pmModelDeployment, wip, autonomy, maxRounds, updatedBy, updatedAt. Edited in the UI and audited.
- **WorkItem:** id, repoId, issueNumber, issueNodeId, projectItemId, state, priority, size, attempts, prNumber, headSha, codingModel (resolved), orchestrationInstanceId
- **AgentSession:** id, workItemId, mode (`Implement` | `Fix` | `ResolveConflict`), copilotTaskId, triggerCommentId, model, startedAt, endedAt, state (from the agent tasks API), sessionUrl (GitHub session log), commitShas[], usage (tokens or cost, where GitHub reports it; spike S13)
- **ReviewRound:** id, workItemId, round, headSha, copilotReviewId, findings[]
- **Finding:** fingerprint, severity (High/Medium/Low), path, line, ruleHint, body, status (`Open` | `Addressed` | `Repeated` | `Contested` | `Waived`), firstSeenRound, lastSeenRound
- **GateEvaluation:** id, workItemId, headSha, profile, inputs (snapshot), result, reasons[]
- **Approval:** id, workItemId, headSha, kind (L1 risk, acceptance test, template upgrade, model-check escalation), decision, reason, decidedBy, decidedAt
- **InboxEvent:** deliveryId, event, receivedAt, processedAt (webhook de-duplication in the lean profile)
- **BoardSnapshot:** projectId, itemId, status, seenAt (used by the board poller)
- **SpecVersion:** id, projectId, version, gitSha, tag, publicSafeResult
- **Requirement:** id (`REQ-nnn`), projectId, featureId (optional), text, introducedIn, status, linked issues, linked acceptance tests
- **FeatureSpec:** id, projectId, title, slug, path, state (`Draft` | `Clarifying` | `Planned` | `Approved` | `Applying` | `Applied` | `Withdrawn`), specVersion, epicIssueNumber, changeProposalId, intakeInstanceId, sourceIssues[]
- **InboxItem:** repoId, issueNumber, author, state (`New` | `Dismissed` | `Planned` | `Done`), featureId, suggestedGroup
- **ChangeProposal:** id, kind (`spec-edit` | `feature`), fromSpecVersion, toSpecVersion, items[] (add, update, close, CR), state, appliedBy
- **LineState:** repoId, state (`Running` | `LineStopped`), cause, suspectPrs[], stoppedAt, restoredAt
- **PreviewEnvironment:** repoId, prNumber, appName, url, imageDigest, state, createdAt, deletedAt
- **AuditEvent:** append-only log. Who or what, action, before and after, correlation IDs (issue, session, PR, SHA).

---

## 8. Orchestration (deterministic)

### 8.1 `ProjectScheduler` (eternal orchestration, one per repository)

The scheduler is triggered by `IssueClosed`, `PrMerged`, `DependencyChanged`, `StatusChanged` (from the board poller or the UI), `SlotFreed` or a timer. It then:

1. **Candidates:** project items with Status = Ready, not paused, not already in an orchestration, and whose issue passes the work-label rule (§5.6): it carries the label, the label was applied by you or the factory, and you or the factory created the issue.
2. **Hard dependency filter:** drop any item with an open `blockedBy` issue. Read through GraphQL `blockedBy` and `issueDependenciesSummary`, or REST `/issues/{n}/dependencies/blocked_by`. Dependencies may be cross-repository.
3. **Cycle check:** run Tarjan's SCC algorithm over the dependency graph. Every item in a cycle goes to `Blocked`, reason `DependencyCycle`.
4. **Ranking** (deterministic, stable):
   1. Priority (Critical > High > Medium > Low)
   2. **Unblock count:** the number of transitive dependents this item unblocks (critical-path first)
   3. Size (Small first, for throughput)
   4. Age (oldest first)
   5. Issue number (tie-break)
5. **Concurrency filter:** respect the repo WIP limit (default 1 in v1). With WIP > 1, skip candidates whose predicted touch-set (PM agent estimate of paths or modules) overlaps an in-flight item by more than the threshold.
6. Start a `WorkItemOrchestration` for each selected item.

The PM agent may **suggest** a re-ranking (for example, "#14 should go before #13 because…"). The suggestion is shown in the UI and applied only if policy `pm.canReorder = true`. It is limited to reordering inside the same priority band and can never override step 2.

### 8.2 `WorkItemOrchestration` (one per issue)

```text
1  SpecReview      → PM agent (Foundry, structured output) → Ready | NeedsClarification(questions)
                     NeedsClarification: comment questions on the issue, board → Blocked, END
2  Claim           → board → In Progress; set Factory State / Agent / Model fields
3  Assign          → [your delegated token] assign issue to copilot-swe-agent[bot] with
                     agent_assignment { target_repo, base_branch, custom_agent: "factory-coder",
                                        custom_instructions: <factory + acceptance criteria + contract>,
                                        model: ResolveModel(workItem) }
                     await PrOpened (draft) → record PR; await CopilotWorkFinished + PrReadyForReview
                     (timeout 90m; session waiting_for_user → NeedsClarification; failed → retry once)
4  Arm             → [your delegated token] enable auto-merge (squash); board → In Review
5  loop round = 1..MaxRounds
     await ChecksCompleted(headSha) + CopilotReviewSubmitted(headSha)       (timeout 30m)
     findings = Normalize + Fingerprint(review comments written by the Copilot reviewer only)
     triage   = PM agent classifies findings as actionable / false-positive (advisory, logged)
     gate     = EvaluateGate(profile, headSha, checks, findings, diff, approvals, policy)   // pure
     [App token] post check-run "demo-factory/review-gate" = gate.Result on headSha
     if gate.Pass → AwaitingMerge; break
     if gate.OnlyApprovalMissing → AwaitingApproval; await Approved | ChangesRequested (UI)
     if gate.HasContestedHigh or round == MaxRounds → Blocked(escalate); END
     set the PR to draft            // no reviews, CI noise or merges while Copilot works (spike S14)
     [your delegated token] post ONE batched PR comment:
        "@copilot please address the following and push to this branch:
         F-1a2b [High] src/Auth.cs:42 …   F-9c8d [Medium] …   CI: test 'UserApiTests' failed: <excerpt>"
     await CopilotWorkStarted (👀 / timeline) → CopilotWorkFinished
     mark the PR ready for review; re-arm auto-merge if GitHub switched it off
6  await PrMerged (timeout 2h)
     | NewPush       → back to 5
     | BehindMain    → update the branch through the API (no Copilot session), back to 5;
                       if the PR's own diff is unchanged, the last review round is carried over
     | MergeConflict → @copilot "merge main and resolve conflicts", back to 5
7  Done            → board → Done; raise DependencyChanged to ProjectScheduler
```

Only **one Copilot session per PR** is active at any time. The orchestrator never posts a new `@copilot` comment while a session is `queued` or `in_progress`.

Every activity is idempotent and keyed by `(workItemId, step, round)`. GitHub writes use conditional checks (for example, do not move the card if it is already in the target column).

### 8.3 The review gate (pure function)

`demo-factory/review-gate` is a **required status check** in the target repo ruleset, and the ruleset pins its source to the Demo-Factory App. A check with the same name from any other source, such as a workflow, does not count. It passes only when **all** of these hold:

| # | Condition |
|---|---|
| 1 | Evaluated SHA == current PR head SHA |
| 2 | Every required check (build, test, lint, …) is `success` on that SHA, reported by GitHub Actions |
| 3 | A review by the Copilot reviewer exists for that SHA. After a branch update that leaves the PR's own diff unchanged, the previous round's review is carried over. |
| 4 | Count of `Open` findings with severity ≥ `policy.blockingSeverity` (default Medium) == 0 |
| 5 | No `Contested` High findings |
| 6 | No changes under protected paths: `.github/**`, `scripts/**`, lint and test configuration, `CODEOWNERS`, `docs/spec.md`, `docs/features/**`, the acceptance harness, and the acceptance tests of every merged AT issue. Push rulesets aren't available on a personal account, so **this condition is the enforcement point**: the PR can't merge, and the factory asks Copilot to revert those files. |
| 7 | Diff size ≤ `policy.maxChangedLines` (default 1,500) |
| 8 | Spec conformance (optional, a model check): the PM agent compares the diff with the acceptance criteria. `fail` blocks the PR. `pass` adds nothing to the other conditions. |
| 9 | Code scanning on the head SHA is complete, with no new alerts at or above the threshold (covers the Dependabot gap in ruleset merge protection) |
| 10 | Dependency review passed; for Dependabot PRs, the Dependabot profile in §5.4 applies |
| 11 | **Acceptance tests first** (§6.1): the feature's acceptance suite passes on the head SHA; its acceptance-test files are unchanged; only its own ID was removed from `pending.json` |
| 12 | **Preview** (§6.4): the trusted `preview.yml` run for the head SHA succeeded, as read by the factory from the Actions API (repos with a deploy target) |
| 13 | **Line not stopped** (§6.2). The `revert` and `line-fix` profiles are exempt. |
| 14 | **Your approval**, recorded in the factory UI for the head SHA, where one is required: risk labels at L1, AT PRs at L1, template upgrades, and any model check that blocked. A GitHub review approval is not used, because your approval of a PR that Copilot opened for you doesn't count on GitHub. |
| 15 | **Trusted origin** (§11.4): the PR's head branch is in the repo itself, and its author is the Copilot agent, Dependabot or you. A PR that implements an issue counts only if that issue still passes the work-label rule (§5.6). |

**Model checks.** Conditions 8, the AT mapping check (§6.1) and the public-safe check (§6.3) are the only places a model takes part in the gate. Each can only fail. A failure gets one fix round; if it fails again, the item waits for you (`AwaitingApproval`), and you either approve it or request changes.

**Gate profiles:**

| Profile | Conditions |
|---|---|
| `feature` | All |
| `acceptance-test` | §6.1 step 3, plus 1, 2, 6, 7, 9, 10, 13, 14 (at L1) and 15 |
| `dependabot` | §5.4, plus 1, 2, 9, 10, 13 and 15 |
| `security-fix` (CodeQL fixes by Copilot) | All except 11 |
| `revert` | 1, 2 and 15. Review findings are advisory. Exempt from 13. |
| `line-fix` | All except 13 |
| `spec` | Docs-only diff (`docs/spec.md`, `docs/features/**`, `docs/adr/**`), opened by you or by the factory on your behalf, public-safe check, 1, 2 and 15 |
| `template-upgrade` | 1, 2, 9, 10, 13, 14 (always) and 15. Exempt from 6. |

Because this gate is a *required check*, merges stay native: auto-merge waits on it like any other check.

### 8.4 Finding fingerprinting and loop protection

Only comments written by the Copilot reviewer become findings. Copilot can repeat comments on re-review, even ones already resolved. The factory therefore:
- Fingerprints each finding: `sha1(path + normalized(ruleHint or first sentence) + severity)`, with line-shift tolerance (±10 lines, the same hunk).
- Tracks status across rounds:
  - The fix agent addresses a finding → `Addressed`.
  - It is raised again after being addressed → `Repeated`.
  - It is repeated twice → `Contested`.
- `Contested` Low or Medium findings are treated as non-blocking. The agent posts a justification reply on the thread, and the finding is recorded in the audit log.
- A `Contested` High finding sends the item to `Blocked` for a human.
- `MaxRounds` defaults to 3. After the last round the item goes to `Blocked`, with a summary comment on the PR.

### 8.5 Retry classification

| Failure | Detection | Action |
|---|---|---|
| Transient (GitHub 5xx, rate limit, model throttling) | HTTP status, `Retry-After` | Durable retry with backoff (max 5) |
| Copilot session `failed` or `timed_out` | Agent tasks API state, missing `CopilotWorkFinished` | Re-trigger once (new assignment or `@copilot` comment), then `Failed` |
| Copilot session `waiting_for_user` | Agent tasks API state | `NeedsClarification`, and post the agent's question on the issue |
| Implementation failure (CI red after fix) | Gate | Counts as a round |
| Missing requirements | PM agent at spec review | `NeedsClarification` |
| Policy violation (protected path touched) | Gate condition 6 | One `@copilot` comment asking it to revert, then `Blocked` |
| Unresolvable dependency or cycle | Scheduler | `Blocked` |

---

## 9. Agents

### 9.1 Project Manager agent (in the factory)
- **Runtime:** a Microsoft Agent Framework agent on a Foundry model deployment (managed identity, EU region).
- **Responsibilities:**
  - `GeneratePlan(spec)` → the first plan for a new project (§5.1): requirement IDs, architecture outline, milestones, issues, acceptance-test issues and dependencies.
  - `PlanIncrement(featureSpec, projectContext)` → the same for a feature added to an existing project (§5.5), including links to existing issues and overlaps with existing requirements.
  - `ProposeChanges(oldSpec, newSpec, backlog)` → the change proposal after a spec edit (§6.3).
  - `GroupInbox(issues)` → suggested groupings and likely duplicates among reported issues (§5.6). Advisory only.
  - `DraftFeatureFromIssues(issues)` → a draft feature spec. The issue text is passed as quoted, untrusted data.
  - `ReviewSpec(issue)` → `{ verdict: Ready|NeedsClarification, missing: [], questions: [] }`
  - `EstimateTouchSet(issue, repoTree)` → `{ paths: [], modules: [], confidence }`. It uses the repo tree and file list from the GitHub API, not a clone.
  - `ComposeInstructions(issue)` → the `custom_instructions` for the Copilot assignment: acceptance criteria, scope boundaries, the interface contract from the acceptance-test issue (§6.1) and a "definition of done" checklist.
  - `TriageFindings(findings, diff)` → `actionable` or `likely false positive`, with a reason. This is **advisory only**; the gate still uses severity and status.
  - `CheckConformance(issue, diff)` → `{ verdict, unmetCriteria: [] }`. A model check: it can block, never approve (§8.3).
  - `CheckPublicSafe(spec)` → flags for customer names, internal URLs, credentials and personal data. Also a model check.
  - `SuggestDecomposition(issue)` → proposed sub-issues. These are **created only after human approval in the UI**.
  - `SuggestReorder(readySet)`. Advisory only (§8.1).
- **Traceability:** every PM agent output is stored with its prompt version and model deployment, so a decision can be reproduced.
- **Definition of Ready (checked by the PM agent):** problem statement, acceptance criteria (testable), scope boundaries, and a `blocked by` link for anything it depends on.

### 9.2 Coding agent: Copilot cloud agent (in GitHub)
- **Start:** the issue is assigned to `copilot-swe-agent[bot]` through the issues REST or GraphQL API with `agent_assignment`:

| Field | Value |
|---|---|
| `target_repo` | The target repository |
| `base_branch` | Default branch (or the configured integration branch) |
| `custom_agent` | `factory-coder`, a custom agent profile kept in the target repo at `.github/agents/factory-coder.md` |
| `custom_instructions` | Generated by the PM agent (§9.1) |
| `model` | `ResolveModel(workItem)`: work-item override → repo setting → global default. **Default: Gemini 4 Carbon.** |

- **Iterations:** batched `@copilot` PR comments posted with your delegated token (§10.1). Copilot pushes to the same branch, remembers context from earlier sessions on the PR, and keeps the same custom agent. A comment from the web UI can pick a different model, but by default Copilot reuses the model that created the PR.
- **Built-in quality:** by default the cloud agent runs its own security checks and a Copilot code review pass before it finishes, and fixes what it finds. This lowers the number of factory rounds.
- **Environment:** the target repo controls the agent's build environment with `.github/workflows/copilot-setup-steps.yml` (SDKs, tools, caches) and its firewall allowlist.
- **`factory-coder` profile** (template shipped by the factory, installed by the onboarding wizard). It tells the agent to:
  - Implement only the scope in the instructions.
  - Never edit protected paths, including locked acceptance tests.
  - Follow the interface contract in the instructions.
  - Run the repo's `scripts/quality` before finishing.
  - List each finding ID it addressed in the commit message, or explain why it disagrees, so the factory can track findings.
  - Stop and ask, instead of guessing, if the spec is ambiguous.

### 9.3 Reviewer: Copilot code review (setting)
- Copilot code review is **a repository ruleset setting**: "Automatically request Copilot code review", with **Review new pushes** on and **Review draft pull requests** off. It therefore runs on every PR, including the cloud agent's, as soon as the PR is ready, and again on every push. The onboarding wizard checks this setting. The factory does not trigger reviews.
- **Watchdog only:** if no review exists for the head SHA after 15 minutes, the factory raises an operational alert and requests `copilot-pull-request-reviewer[bot]` once through the REST API. This guards against service hiccups; it is not part of the normal flow.
- Copilot review comments carry High, Medium or Low severity. The gate uses these severities directly.

### 9.4 Model configuration
- **Coding model:** selected in the UI at three levels: global default, per repo, and per work item (override). Resolution order is item → repo → global. The value is passed as `model` on assignment.
- **Model catalogue:** the dropdown is populated from a factory-maintained catalogue (display name, API model ID, enabled flag, notes such as "usage-based billing"). Admins edit the catalogue in the UI. The catalogue is curated, because no list-models endpoint for the cloud agent has been identified. Each entry is validated with a smoke test (a trivial task in a sandbox repo) before it can be enabled.
- **Plan check:** a model must also be included in your Copilot plan. The smoke test is the check.
- **PM model:** the Foundry deployment name, also selectable in the UI (global and per repo).
- Every change to a model setting is audited and shows up on affected work items' timelines.

## 10. GitHub integration

### 10.1 Identities: sign in with GitHub, act on your behalf

The factory uses one **GitHub App** (the "Demo-Factory App", owned by and installed on your personal account with access to **all repositories**, so new repos are covered automatically). It gives the factory two kinds of token:

| Token | How it's obtained | Used for |
|---|---|---|
| **User access token (delegated, "on your behalf")** | You click "Sign in with GitHub" in the UI (GitHub App web flow). Expiring tokens are on: the access token lasts 8 hours and the refresh token 6 months. | Everything the factory *does* in your name: creating repos from templates, repo settings, rulesets, security settings, projects, issues, dependencies, assigning issues to Copilot, `@copilot` comments, enabling auto-merge, agent tasks API |
| **Installation token** (server-to-server) | JWT signed with the App's private key in Key Vault | Plumbing that must not depend on your session: **webhooks**, posting the `demo-factory/review-gate` **check run**, read-only reconciliation |

**Why this works well for a personal account:**
- A user access token is limited to the permissions that **both** the App and you have. As owner of your repos you have admin, so bootstrap (repo creation, rulesets, settings) works without a separate admin App or machine account.
- Copilot only responds to `@copilot` from people with write access, and the agent APIs need user tokens. Both are satisfied, because the actor is **you**.
- GitHub shows every action as *you, via Demo-Factory App*, which gives a clear audit trail.
- Copilot usage, including usage-based billing for Gemini 4 Carbon, goes to **your** Copilot plan. The budget caps in §12 apply.

**Scope.** Installing the App on all repositories means the delegated token can reach every repo in your account, not only factory repos. The factory acts only on repos it created or onboarded (an allowlist in SQL), and every call is audited. To narrow the scope on GitHub's side, use a separate organization (§10.4).

**Unattended operation (the factory runs while you're offline):**
- The refresh token is stored encrypted (Key Vault key) and used by a **TokenBroker** (one durable entity per user). Each refresh returns a new refresh token and invalidates the old one, so only the broker refreshes. This avoids two workers racing and locking each other out.
- A refresh is one durable operation: the new refresh token is stored before the old access token is dropped. If the factory crashes after GitHub has rotated the token but before it is stored, both tokens are lost. The factory then pauses and asks you to reconnect.
- When you revoke the App's authorization on GitHub, the App receives `github_app_authorization` and the factory pauses at once.
- If the refresh fails (revoked, expired after 6 months unused, or the App uninstalled), all projects **pause** with a "Reconnect GitHub" banner and a notification. Nothing falls back to another identity.
- "Sign out everywhere" in the UI revokes the token (`DELETE /applications/{client_id}/token`) and pauses the factory.

**UI access:** only GitHub logins on an allowlist (by default, just your account) can sign in. Roles (Viewer, Operator, Admin) are mapped per GitHub login.

**Demo-Factory App permissions** (repository): Administration RW (create repos, rulesets, settings), Contents RW (docs only: the spec, feature specs and ADRs; plus revert PRs and branch updates), Pull requests RW, Issues RW, Checks RW, Actions R, Workflows (not granted: the factory never writes workflow files after bootstrap; templates are copied by GitHub's template generation), Secret scanning alerts R, Code scanning alerts RW, Dependabot alerts R, Metadata R. **Account:** Projects RW.

**Webhooks:** `issues`, `issue_comment`, `issue_dependencies`, `pull_request` (including `ready_for_review` and `closed`), `pull_request_review`, `pull_request_review_comment`, `check_suite`, `check_run`, `workflow_run`, `push` (for `main`), `code_scanning_alert`, `secret_scanning_alert`, `dependabot_alert`, `repository`, plus `github_app_authorization` and `installation`, which every App receives. `projects_v2_item` is not in the list: GitHub sends it only for organization-level projects, so the board is polled (§4.1).

**Rate limits.** Writes are paced and honour `Retry-After`. The board poller and the reconciler share a GraphQL budget per hour, and the poll interval backs off automatically when the budget runs low.

**The factory never writes application code.** Only Copilot (and Dependabot) do, under the repo's rulesets. The factory's own commits are limited to documents under `docs/`, revert PRs and branch updates from `main`, and they go through pull requests like everything else.

### 10.2 Target repo prerequisites (checked by the onboarding wizard)

**Ruleset on the default branch:**
- Require a pull request
- Required status checks, each **pinned to its source**: build, test and lint from GitHub Actions; `demo-factory/review-gate` from the Demo-Factory App
- Require branches to be up to date (the factory serialises merges; see §10.3)
- **Automatically request Copilot code review**, with **Review new pushes** on and **Review draft pull requests** off
- Require code scanning results (§5.3)
- Block force-pushes and deletions

**Protected paths:** push rulesets aren't available on a personal account, so protected paths are enforced by the factory gate (condition 6). `CODEOWNERS` still documents ownership.

**Account plan:** public repos (the default) work on GitHub Free. GitHub Pro is only needed for the optional private-repo fallback.

**Repository settings:**
- Allow auto-merge, squash merge only, and automatically delete head branches.
- Actions: default `GITHUB_TOKEN` permissions read-only; "Allow GitHub Actions to create and approve pull requests" off; fork pull request workflows need approval for all outside contributors.
- **No repository-level Actions secrets or variables holding credentials.**
- Environments `preview` and `demo`, each with deployment branches restricted to `main`.
- Issue intake: open by default, so other people can report issues (§5.6). The work label exists, and no issue form applies it. Where `intake.acceptOutsideIssues` is false, the interaction limit is set to collaborators only.

**Copilot cloud agent settings:**
- Cloud agent enabled for the repo. Check that `suggestedActors` includes `copilot-swe-agent`.
- **"Require approval for workflow runs" turned off.** By default, Actions workflows don't run automatically when Copilot pushes. A dark factory can't wait for a human click, so this must be off. Compensating controls are in §11.1.
- Built-in validation tools (security checks and Copilot code review self-check) left **on**.
- Do **not** require human PR approvals on factory-managed branches at autonomy L2. The gate is a status check. (The person who asked Copilot to create a PR can't count as an approver anyway.)

**Files:**
- `.github/copilot-instructions.md` and `AGENTS.md` (repo conventions; Copilot review reads them too)
- `.github/agents/factory-coder.md` (custom agent profile, §9.2)
- `.github/workflows/copilot-setup-steps.yml` (agent environment)
- An executable `scripts/quality` entry point (build, lint, test)

**GitHub Project fields:**

| Field | Type / values |
|---|---|
| Status | Backlog / Ready / In Progress / In Review / Blocked / Done |
| Priority | Single select |
| Size | Single select |
| Factory State | Text |
| Attempts | Number |
| Agent | Text |
| Model | Text (resolved coding model) |
| Feature | Text (feature slug; filters the board per feature) |
| Last Run | Date |

Turn on the project's built-in workflows: *Item closed → Done* and *PR merged → Done*. Leave *Auto-add to project* off (§11.4).

### 10.3 Auto-merge
The orchestrator enables auto-merge (GraphQL `enablePullRequestAutoMerge`, squash) **on `ready_for_review`**, when Copilot finishes its first session and takes the PR out of draft. GitHub only offers auto-merge while a PR cannot merge yet, and the `demo-factory/review-gate` check guarantees that at this point. GitHub then merges as soon as the required reviews and checks, including `demo-factory/review-gate`, are satisfied. With WIP > 1, the factory serialises merges itself: one PR in `AwaitingMerge` at a time, and the next one is updated with `main` before it can arm. An update without conflicts goes through the API (`update-branch`), with no Copilot session; `@copilot` is used only to resolve conflicts. GitHub's merge queue is not an option here: it is available only for repos owned by an organization (§10.4).

### 10.4 Option: a free GitHub organization (not the default)

The default stays your personal account. A free organization that you own is worth testing, because it removes three limits:

| On a personal account | With an organization |
|---|---|
| No merge queue; the factory serialises merges | Merge queues are available in public repos owned by an organization |
| No `projects_v2_item` webhooks; the board is polled | Organization-level projects send them |
| The App is installed on every repo in your account | The App is installed on the organization only |

You would still sign in as yourself, and the factory would act on your behalf as the organization's owner. Public repos keep the free security baseline.

Verify first (spike S12) that, in organization-owned repos and on a personal Copilot plan:
- the automatic Copilot code review ruleset works;
- the Copilot cloud agent can be assigned issues;
- usage is billed to your plan.

If all three hold, switching is a configuration change: the repo owner, the project owner and the App's installation target.

---

## 11. Security and governance

### 11.1 Threat model highlights

| Threat | Control |
|---|---|
| The agent weakens CI or tests to pass | Gate condition 6 over an explicit protected-path list (§12): workflows, `scripts/**`, lint and test configuration, the acceptance harness, locked acceptance tests, agent profiles. A daily drift check compares these files on `main` with the template. |
| A workflow changed on a PR branch runs with more rights. Workflow approval is off, and Copilot's branches are in the repo, so such a workflow can request write scopes on its `GITHUB_TOKEN`. | Nothing valuable is reachable with that token: there are **no repository secrets**; the `preview` and `demo` environments are restricted to `main`, so it can't get an Azure token; `main` is protected by the ruleset; Actions can't approve PRs; deploys use digests built in the same trusted run, so an overwritten image tag is never used. The change itself can't merge (condition 6). |
| A spoofed result: a workflow posts a check or status with the gate's name | Required checks are pinned to their source (§10.2). The factory reads preview results from the Actions API, not from a commit status (§6.4). |
| PR code runs with deployment credentials | The preview workflow is split (§6.4): the job with credentials never checks out or runs PR code, and tests run without credentials, from `main`. There are no `pull_request_target` workflows. |
| Outside contributors on a public repo: issues, comments, fork PRs | The trust boundary in §11.4 |
| Prompt injection through issue or PR text | Only issues that pass the work-label rule reach the coding agent, and their conversations are locked (§5.6). Reported issues reach the PM agent only as quoted data, and you review what it drafts from them. Copilot cloud agent runs in its sandbox behind its firewall. The factory treats text as data and never executes it. PM agent outputs are schema-validated. |
| Theft of your delegated token (it can act as you on all your repos) | Refresh token encrypted with a Key Vault key and only decrypted inside the TokenBroker. Access tokens are cached in memory only (8-hour life). The App is limited to the permissions listed in §10.1. Every call is audited. One-click revoke. **Enable 2FA or passkeys** on your GitHub account. |
| Runaway cost | Session caps and spend caps per repo and per item (§12), maximum rounds, a global kill switch, usage reports per model, and a hard budget set in GitHub billing as the backstop |
| Data residency | The coding model (Gemini 4 Carbon through Copilot) and Copilot review process code in GitHub's service, outside your Azure tenant. Record this in the data-flow register. Pick another catalogue model per repo where policy requires it. PM and triage stay on Foundry in the EU. |

### 11.2 Guardrails moved to GitHub
With coding in GitHub, the guardrails that a local permission handler used to enforce are now GitHub settings:
- The cloud agent firewall and `copilot-setup-steps.yml` control what the agent can reach and install.
- The factory gate blocks merges that touch protected paths (no push rulesets on personal accounts).
- Repository settings keep workflow tokens read-only by default, and environments keep Azure sign-in on `main`.
- Required checks and the `demo-factory/review-gate` check control merging.

The onboarding wizard verifies all of these, and the reconciler re-checks them daily (drift detection). If a guardrail drifts, the repo is **paused automatically**.

### 11.3 Autonomy levels (per repo policy)
- **L0 Shadow:** the factory opens PRs but never enables auto-merge.
- **L1 Supervised:** auto-merge except for labelled risk areas (`area:auth`, `area:infra`, dependency bumps) and acceptance-test PRs, which need your approval in the factory UI (gate condition 14).
- **L2 Dark:** fully unattended. Approval is still required for template upgrades and for any model check that blocked.

### 11.4 Trust boundary for public repos

Anyone can open issues, comment and send pull requests to a public repo. The factory treats only these actors as trusted: **you**, the **Copilot coding agent**, the **Copilot reviewer**, **Dependabot** and the **Demo-Factory App**.

| Input | Rule |
|---|---|
| Issues | Only issues that pass the work-label rule are scheduled (§5.6). Everything else is ignored and shown in the issue inbox. The project's auto-add workflow is off, so nothing reaches the board unasked. |
| Issue and PR comments | The factory locks the conversation on every issue and PR it manages. Any other comment is ignored unless written by a trusted actor. `AT-DEFECT` counts only from the Copilot agent. Answers to clarification questions count only from you. |
| Review findings | Taken only from the Copilot reviewer. Other reviews are shown in the UI but never enter a fix round. |
| Pull requests | Only PRs from branches in the repo, opened by the Copilot agent, Dependabot or you, are managed. Fork PRs get no orchestration, no preview and no auto-merge (gate condition 15), and their workflows need your approval to run. |
| Repo interactions | Open by default, so other people can report issues. With `intake.acceptOutsideIssues: false`, the interaction limit is set to collaborators only; it expires after six months at most, so the factory renews it and alerts you if the renewal fails. |
| Preview and demo URLs | Public and unauthenticated by design: synthetic data only, one replica, capped resources, no secrets and no Azure identity. |

---

## 12. Configuration

Configuration has two layers:
- **Structural policy** (gate rules, protected paths, risk labels) lives in the factory repo (GitOps) as `policies/repos/<owner>__<repo>.yml`. Changes go through a PR on the factory repo.
- **Runtime settings** (coding model, PM model, WIP, autonomy, max rounds, pause) are edited directly in the UI, stored in Azure SQL and audited.

```yaml
repository: <you>/<repo>
project: { owner: <you>, number: 7 }
autonomy: L1
wip: 1
boardPollSeconds: 60
workLabel: factory             # only issues with this label are built (§5.6)
intake: { acceptOutsideIssues: true, closeSourceIssuesWhenDone: true }
models:                 # runtime values are edited in the UI (RuntimeSettings); these are defaults
  coding: gemini-4-carbon   # catalogue ID; exact API model string confirmed in spike S2
  pm: foundry/<eu-deployment-name>
review:
  blockingSeverity: Medium
  maxRounds: 3
  contestedPolicy: { low: allow, medium: allow, high: block }
gate:
  requiredChecks: [build, test, lint]
  maxChangedLines: 1500
  specConformance: true        # a model check: it can block, never approve
acceptanceTests: { separateIssueMinSize: Medium, maxFixes: 2 }
protectedPaths:
  - ".github/**"
  - "scripts/**"
  - "tests/acceptance/_harness/**"
  - "docs/spec.md"
  - "docs/features/**"
  - "CODEOWNERS"
  - ".editorconfig"
  - "eslint.config.*"
  - "**/*.ruleset.json"
  # plus tests/acceptance/<ISSUE-ID>/** for every merged acceptance-test issue (added automatically)
trustedActors: [owner, copilot-swe-agent, copilot-pull-request-reviewer, dependabot]
budget:                        # example values
  dailySessions: 40
  maxSessionsPerItem: 5
  dailySpendUsd: 25            # spend caps depend on spike S13
  spendPerItemUsd: 5
riskLabels: [area:auth, area:infra, deps]
```

**Budgets.** Gemini 4 Carbon is billed per token, so a session count is only a proxy for cost. Where GitHub reports usage (spike S13), the factory enforces the spend caps. In every case, set a hard budget in your GitHub billing settings as the backstop.

---

## 13. API (factory-api)

| Method and route | Purpose |
|---|---|
| `POST /webhooks/github` | GitHub webhook receiver (HMAC verified) |
| `GET /api/repos` · `POST /api/repos` | List repos; onboard an existing repo (runs readiness checks) |
| `POST /api/projects` · `POST /api/projects/{id}:clarify` · `GET/PUT /api/projects/{id}/plan` · `POST /api/projects/{id}:approve` · `GET /api/projects/{id}/bootstrap` | New project: intake, clarification, plan edit and approval, bootstrap status |
| `GET /api/templates` | Golden-path templates and versions |
| `GET/POST /api/projects/{id}/features` · `POST /api/features/{id}:clarify` · `GET/PUT /api/features/{id}/plan` · `POST /api/features/{id}:approve` · `GET /api/features/{id}/status` · `POST /api/features/{id}:withdraw` | Add a feature to an existing project: intake, clarification, increment edit and approval, apply status, withdraw |
| `GET /api/projects/{id}/inbox` · `POST /api/inbox/{id}:dismiss` · `POST /api/projects/{id}/features:from-issues` | Issue inbox: issues without the work label; dismiss; start a feature from selected issues (§5.6) |
| `GET/PUT /api/projects/{id}/spec` · `GET /api/projects/{id}/requirements` · `GET/POST /api/projects/{id}/change-proposals` · `POST /api/change-proposals/{id}:apply` | Living spec, coverage, change proposals |
| `GET /api/repos/{id}/line` · `POST /api/repos/{id}/line:resume` | Stop-the-line state (manual resume is Admin only) |
| `GET /api/previews` · `DELETE /api/previews/{id}` | Preview environments |
| `GET /api/repos/{id}/readiness` | Ruleset, settings and project field check results |
| `GET /api/repos/{id}/board` | Board snapshot with factory overlay |
| `GET /api/repos/{id}/graph` | Dependency DAG (nodes, edges, critical path) |
| `GET /api/workitems/{id}` | Detail: timeline, sessions, rounds, findings, gate evaluations, approvals |
| `POST /api/workitems/{id}:retry` · `:cancel` · `:unblock` · `:waive-finding` · `:mark-ready` | Operator actions (role-gated, audited) |
| `GET /api/approvals` · `POST /api/workitems/{id}:approve` · `:request-changes` | Approvals inbox; record your decision for the head SHA (gate condition 14) |
| `GET /api/sessions?filter…` · `GET /api/sessions/{id}` | Copilot sessions (state, model, commits, link to the GitHub session log) |
| `GET /api/models` · `PUT /api/models/{id}` | Model catalogue (Admin) |
| `GET/PUT /api/settings` · `/api/repos/{id}/settings` · `/api/workitems/{id}/settings` | Runtime settings, including the coding model (Operator or Admin) |
| `GET /auth/github/login` · `GET /auth/github/callback` · `POST /auth/logout` · `POST /auth/revoke` | Sign in with GitHub (web flow), sign out, revoke the delegated token |
| `POST /api/factory:pause` · `:resume` | Kill switch (global, or per repo with `?repo=`) |
| `GET /api/metrics` | KPIs |
| `GET /api/audit?…` | Audit log query |
| Hub `/hubs/factory` | Realtime: `workItemChanged`, `sessionEvent`, `gateEvaluated`, `approvalRequested`, `lineStateChanged`, `factoryStatus` |

Auth: GitHub sign-in with an HTTP-only session cookie (backend for frontend). The API resolves the GitHub login to a role on the allowlist: **Viewer**, **Operator** (retry, cancel, unblock, waive, approve) or **Admin** (projects, policies, kill switch).

---

## 14. Web UI specification

### 14.1 Look and feel
- Fluent 2 throughout. `FluentProvider` with a custom brand ramp generated by `createLightTheme` and `createDarkTheme`.
- **Theme modes:** System (default, follows `prefers-color-scheme`), Light, Dark. Persisted per user. Switching is instant with no reload, because tokens are CSS variables. Supports `forced-colors` for Windows high contrast.
- Shell: a collapsible `NavDrawer` on the left, a top bar with repo switcher, global search (`Ctrl+K`), factory status pill (Running, Paused or Degraded), theme toggle and avatar.
- Density: comfortable or compact toggle for data-heavy views.
- Motion: subtle Fluent motion tokens on card moves and status changes; reduced-motion respected.
- Loading: `Skeleton` placeholders, never spinners on full pages. Designed empty states with a call to action.
- Accessibility: WCAG 2.2 AA, full keyboard support, and live regions for realtime status updates.

### 14.2 Screens

**0. New project wizard and plan review**
- **Steps:** name and spec editor (Markdown with preview, file upload), then clarifying questions (chat-style, answered inline), then stack template, autonomy, coding model and deploy target (repos are public by default), then **plan review**, then launch.
- **Plan review:** an editable backlog table (title, acceptance criteria, size, priority, milestone) next to a live dependency graph. Validation flags missing acceptance criteria, cycles and oversized items. A cost and duration forecast updates as you edit.
- **Launch:** a provisioning timeline that shows each bootstrap step live, with retry and abort.

**0a. Add feature** (on every project page, and from the Spec screen)
- **Steps:** start from a blank spec or from issues selected in the inbox; then title and feature spec editor (Markdown with preview, file upload), clarifying questions, **increment review** and apply.
- **Increment review:** the plan-review screen with the existing backlog dimmed and the new items highlighted, in both the table and the dependency graph. Links to existing issues are drawn across that boundary.
  - Warnings: overlap with an existing requirement, an affected item in progress, missing acceptance criteria, cycles.
  - Controls per item: Ready or Backlog, priority, milestone.
- **Apply:** a short timeline (spec PR, issues, board), with retry and abort.
- **Features list:** every feature of the project with its state, progress (issues done of total), requirement coverage, epic link and a "Withdraw" action.

**0b. Spec**
- A Markdown editor with live preview and requirement IDs highlighted.
- A coverage panel per requirement: planned, in progress, tests written, tests passing, done.
- Version history with diffs, plus "Propose change", which opens the change proposal in the plan-review screen.

**0c. Andon banner and previews**
- A global red banner while a line is stopped (§6.2), with cause, suspects, actions and timer.
- A **Previews** page: live preview apps with URL, PR, age, and a "Delete" action.
- A **Showcase** button: opens the demo URL alongside board and coverage, for presenting.

**0d. Issue inbox** (per project)
- Every open issue without the work label: title, author, age, reactions and labels, with the PM agent's suggested groups and likely duplicates.
- Actions: select one or more and "Create feature" (opens the Add feature wizard with a drafted spec), "Dismiss", "Open in GitHub".
- An issue that is already part of a feature shows that feature and its progress.
- A banner explains the rule if you label someone else's issue: it can't be built directly, use "Create feature".

**1. Overview (dashboard)**
- KPI cards:
  - Throughput (merged per day)
  - Median lead time (Ready → Done)
  - First-pass gate rate
  - Average review rounds
  - Blocked count
  - Copilot sessions and spend today against budget
  - Mean time to restore (stop the line) and flaky-test count
  - Requirement coverage (% of requirements with passing acceptance tests)
- **Approvals inbox:** everything waiting for you: L1 approvals, acceptance-test approvals, model checks that blocked, and change proposals.
- Live activity feed: Copilot sessions, gate results, merges.
- Repo health tiles: readiness state, WIP, queue depth, and the number of new issues in the inbox.
- Trend charts: throughput, rounds per item and cost per item over 30 days.

**2. Board**
- Kanban mirror of the GitHub Project (Backlog, Ready, In Progress, In Review, Blocked, Done), with a filter per feature.
- Only issues that pass the work-label rule are managed. A card without it is greyed out and marked "Not managed by the factory".
- Each card shows priority, size, dependency badge (blocked by N / unblocks M), a live Copilot-session ring, the coding model chip, the current round (for example "Round 2/3") and a PR link.
- Humans may drag only between Backlog, Ready and Blocked. Agent-owned columns are locked, with a tooltip explaining why.
- "Mark ready" on a card is picked up at once. A drag on GitHub itself is picked up by the board poller within about a minute.

**3. Dependency graph**
- A DAG (React Flow) coloured by status. The critical path is highlighted, and the "next up" items are pulsing.
- Clicking a node opens the work item. Cycles are shown in red, each with a resolution hint.

**4. Work item detail** (a split view)
- Left: the issue spec with acceptance criteria and a PM verdict badge.
- Right: tabs.
  - **Timeline:** state transitions with timestamps and actors.
  - **Sessions:** each Copilot session with state (live from webhooks plus agent tasks polling), model, trigger comment, commits and a deep link to the session log on GitHub.
  - **Review rounds:** a findings table per round with severity, status, fingerprint history and a diff link.
  - **Checks:** required checks for the head SHA.
  - **Gate:** each condition ✓/✗ with evidence.
  - **Model and cost:** the resolved coding model (with an override dropdown), session count and duration.
- Actions: Approve, Request changes, Retry, Cancel, Unblock, Waive finding (with a mandatory reason), Open in GitHub.

**5. Sessions**
- A `DataGrid` of Copilot sessions with filters (repo, mode, state, model, date), sortable, with virtualised rows.

**6. Repositories and onboarding wizard**
- Steps: install the GitHub App, choose the project, run the readiness checklist (each item shows a fix link or "Create for me" where the App can apply it), pick an autonomy level and the coding model, and confirm.
- The checklist covers:
  - Copilot review ruleset and Copilot cloud agent enabled
  - required checks, each pinned to its source
  - workflow-approval setting, read-only default workflow token, no repository secrets
  - `preview` and `demo` environments restricted to `main`
  - work label present, issue forms that don't apply it, project auto-add off
  - auto-merge allowed
  - `factory-coder` profile and `copilot-setup-steps.yml` present
- The wizard offers to open a PR that adds the profile and setup steps.

**7. Policies and models**
- **Models:** global default coding model and PM model pickers, per-repo overrides, and the model catalogue editor (Admin), showing smoke-test status.
- **Runtime settings:** WIP, autonomy level, max rounds and pause, per repo, editable inline.
- **Structural policy:** a read view of the YAML with a validation status. "Edit" opens a PR against the factory repo.
- **GitHub connection:** signed-in account, Copilot plan detected, refresh-token expiry, App installation status, "Reconnect" and "Revoke".

**8. Audit**
- A searchable, append-only log with an export to CSV.

**9. Settings**
- Theme, density, notifications (Teams channel webhook for Blocked and Failed events, plus a daily digest), and personal shortcuts.

### 14.3 Frontend structure
```
web/
  src/
    app/        (providers: Fluent, Session (GitHub sign-in), QueryClient, SignalR, Router)
    theme/      (brand ramp, light/dark/system hook, tokens extensions)
    features/   projects | features | inbox | spec | overview | approvals | board | graph | workitem | sessions | previews | repos | policies | audit | settings
    components/ (StatusPill, SessionRing, SeverityBadge, GateChecklist, SessionTimeline, AndonBanner, KpiCard)
    api/        (typed client generated from the OpenAPI 3.2 document)
    realtime/   (SignalR hub client → TanStack Query cache updates)
```

---

## 15. Factory repository layout

```
demo-factory/
  src/
    DemoFactory.AppHost/           Aspire orchestration (local dev, azd deploy)
    DemoFactory.Api/               webhooks, REST, SignalR
    DemoFactory.Orchestrator/      Durable Task worker: scheduler, work-item orchestration, gate
    DemoFactory.Agents/            PM agent (Agent Framework + Foundry), prompts, output schemas
    DemoFactory.Copilot/           Copilot cloud agent adapter: assign, @copilot comments, agent tasks polling
    DemoFactory.GitHub/            GitHub sign-in, TokenBroker, App JWT (Key Vault), REST + GraphQL clients, rate limiting
    DemoFactory.Domain/            state machine, gate (pure), fingerprinting, ranking
    DemoFactory.Infrastructure/    EF Core, Service Bus, Blob, telemetry
  web/                         React + Fluent UI
  agents/                      pm/ prompts (spec analysis, plan generation, triage)
  templates/                   golden-path template sources (published to template repos by CI)
  policies/repos/              per-repo YAML
  infra/                       Bicep modules + azd
  tests/
    DemoFactory.Domain.Tests/      gate, ranking, fingerprint, state machine (property-based)
    DemoFactory.Orchestrator.Tests/ orchestration replay tests
    DemoFactory.E2E/               against a sandbox account + sample repos
  .github/workflows/           CI/CD for the factory itself
```

The factory is developed with its own discipline: PRs, CI, Copilot review and human approval. **The factory never merges changes to itself.**

---

## 16. Risks and validation spikes (do these first)

| # | Assumption to validate | Fallback |
|---|---|---|
| S1 | Delegated user token refresh works unattended for weeks (TokenBroker, rotation, Copilot assignment and `@copilot` with a GitHub App user token) | Fine-grained PAT in Key Vault (manual rotation) |
| S2 | Gemini 4 Carbon is included in your Copilot plan (Pro+ or Max, not Copilot Pro; the rollout is gradual) and is accepted as `model` on `agent_assignment`; confirm the exact model ID string | The catalogue falls back to the next enabled model, or `auto` |
| S3 | Batched `@copilot` comments posted with your delegated token reliably start a session that addresses all listed findings; `copilot_work_started` and `copilot_work_finished` signals can be read from webhooks or the timeline | Poll the agent tasks API; split large batches |
| S4 | Finding fingerprints are stable across re-reviews, and the agent reports addressed finding IDs in its commit messages | Path + hunk matching; PM agent maps commits to findings |
| S6 | The agent tasks API and the issue-assignment API (both public preview) stay compatible | Isolate them behind `DemoFactory.Copilot`; contract tests run nightly |
| S7 | Data residency of the coding model and Copilot review is acceptable per repo | Per-repo model choice; for restricted repos, autonomy L0 (shadow) |
| S10 | GraphQL `revertPullRequest` works with a delegated user token on personal repos | Create the revert branch through the Git Data API |
| S11 | Previews: the artifact, `workflow_run`, OIDC and GHCR flow runs without stored secrets; a workflow on a PR branch is refused an Azure token; scale-to-zero cost stays within budget | Build in the factory, deploy with the factory's managed identity |
| S12 | Organization option (§10.4): Copilot review ruleset, Copilot cloud agent and billing all work in organization-owned repos on a personal Copilot plan | Stay on the personal account (the default) |
| S13 | Copilot usage (tokens or cost) can be read by API per session or per day for a personal account | Session counts as the proxy, plus the hard budget in GitHub billing |
| S14 | Setting a PR to draft during fix rounds suppresses Copilot reviews and CI noise, and auto-merge can be re-armed afterwards | Leave the PR ready, and evaluate only the SHA at `CopilotWorkFinished` |
| S15 | A GitHub App user token can read and write a user-owned Project (items, fields, `copyProjectV2`) | Organization-owned project (§10.4), or the board lives only in the factory UI |

**Closed:**

| # | Question | Outcome |
|---|---|---|
| S5 | Are `issue_dependencies` webhooks available? | Yes, it is a documented webhook event. The reconciler still polls as a safety net. |
| S8 | Does the Copilot plan include Gemini 4 Carbon? | Merged into S2. |
| S9 | Is a merge queue available for personal-account repos? | No. It is available only for organization-owned repos, so the factory serialises merges (§10.3). |
| — | Does Copilot review run on PRs opened by the agent? | It is a ruleset setting that applies to every PR, so it is a readiness check (§10.2), not a spike. |

## 17. Delivery plan

| Milestone | Scope | Exit criteria |
|---|---|---|
| **M0: Spikes** | S1 to S4 and S12 to S15 against a sandbox account. S10 and S11 run before M4c. | Each spike has a documented go or fallback decision |
| **M1: Walking skeleton** | One repo, WIP 1: assign to Copilot → PR ready → auto-merge with CI only; board moves (poller). The work-label rule (§5.6), the trust boundary (§11.4) and pinned check sources are in from the start, because the repo is public. | 10 trivial issues merged unattended |
| **M2: Review loop** | Copilot review ingestion, fingerprinting, batched `@copilot` fix rounds (findings and CI), review gate check, Blocked handling | ≥ 70% first-pass or ≤ 2-round merges on a 20-issue benchmark |
| **M3: Scheduler** | Dependencies, ranking, cycles, reconciler, PM spec review | Correct ordering on the dependency test suite; zero dependency violations |
| **M4: UI v1** | Overview with approvals inbox, Board, Work item detail with sessions, model settings and catalogue, theme modes | Usability pass; Lighthouse accessibility ≥ 95 |
| **M4b: Bootstrap and features** | Sign in with GitHub and TokenBroker, new project wizard, plan review, add-feature flow with increment review (§5.5), issue inbox and features from reported issues (§5.6), golden-path template v1 (.NET + React), per-repo security baseline, Dependabot policy | A project from spec to first merged walking-skeleton PR with no manual GitHub steps. A feature added to that project produces new backlog items that are picked up without a restart. |
| **M4c: Quality loops** | Acceptance tests first, stop the line (halt, flake check, revert, fix forward), living spec with change proposals, preview and demo environments | A seeded "broken main" recovers without manual steps; a spec change produces a correct change proposal; every PR shows a working preview URL; the negative security tests in §18 (17 to 19) pass |
| **M5: Scale and harden** | WIP > 1 with serialised merges, touch-set conflict avoidance, budgets, autonomy levels, graph view, onboarding wizard, audit export | 3 repos in L1 for 2 weeks without manual intervention beyond Blocked items |

---

## 18. Acceptance criteria (system level)
1. An issue with Status = Ready and no open blockers is picked up within 2 minutes.
2. Board status changes are visible on GitHub within 30 seconds of a factory state change.
3. No PR merges unless `demo-factory/review-gate` and all required checks are green on its head SHA.
4. No PR from the factory modifies a protected path.
5. After a merge, newly unblocked issues are scheduled within 2 minutes.
6. Every merged PR has an audit chain covering issue, runs, review rounds, gate evaluations and merge.
7. Killing any factory container mid-flow causes no lost work and no duplicate Copilot assignment or `@copilot` comment after recovery.
8. The UI renders correctly in light, dark and high-contrast modes and is fully keyboard-operable.
9. Changing the coding model in the UI takes effect for the next assignment without a redeploy, and the change is audited.
10. A new project goes from approved plan to a governed repo (security baseline, rulesets, board, backlog) in under 10 minutes, and its `main` is green.
11. A commit containing a supported secret is rejected at push time, including pushes by Copilot.
12. When the delegated token can't be refreshed, every project pauses within 5 minutes and the UI shows "Reconnect GitHub".
13. No feature PR merges if its acceptance-test files differ from `main`.
14. A red `main` stops all merges within 2 minutes; a single-suspect regression is reverted automatically, and `main` is green again without human action.
15. Applying a change proposal updates the issues, acceptance criteria and board, and every requirement keeps a traceable link to its issues and tests.
16. Every non-draft PR from a trusted author in a repo with a deploy target has a preview URL within 10 minutes of CI passing, and the preview is deleted within 15 minutes of the PR closing.
17. A fork PR, or a PR from an untrusted author, gets no preview, no orchestration and no auto-merge.
18. A workflow on a PR branch that names the `preview` or `demo` environment is refused an Azure token.
19. A check named `demo-factory/review-gate` posted by anything other than the Demo-Factory App does not satisfy the ruleset.
20. While the line is stopped, a revert PR can merge and no other PR can.
21. A model check alone never makes the gate pass: with every model check returning `pass` and any other condition failing, the gate fails.
22. A card moved to Ready on GitHub is seen by the factory within 90 seconds.
23. Adding a feature to an existing project produces one docs PR, issues, dependencies and board items, and nothing else: no repo settings, rulesets, templates or Azure resources are touched.
24. After you approve an increment, its issues are on the board within 5 minutes. Those with no open blockers are scheduled under the normal rules without a restart, and work in progress is unaffected.
25. An issue without the work label is never assigned to Copilot, whatever its author or board status.
26. The work label counts only when you or the factory applied it, on an issue that you or the factory created. A label applied by an issue form, by automation or by anyone else is ignored.
27. "Create feature" on selected inbox issues produces a feature draft that references them. After apply, each source issue links to the feature, and it is closed when the feature's epic closes.

---

## 19. References
- Deployments and environments (deployment branches): https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments
- Securely using `pull_request_target` (and `workflow_run`): https://docs.github.com/en/actions/reference/security/securely-using-pull_request_target
- Webhook events and payloads (`projects_v2_item`, `issue_dependencies`): https://docs.github.com/en/webhooks/webhook-events-and-payloads
- Merging a pull request with a merge queue (availability): https://docs.github.com/github/collaborating-with-pull-requests/incorporating-changes-from-a-pull-request/adding-a-pull-request-to-the-merge-queue
- Events that trigger workflows (`workflow_run`, `pull_request_target`): https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows
- Refreshing GitHub App user access tokens: https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/refreshing-user-access-tokens
- About rulesets (plan availability): https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets
- Automating Dependabot with GitHub Actions: https://docs.github.com/code-security/supply-chain-security/keeping-your-dependencies-updated-automatically/automating-dependabot-with-github-actions
- Code scanning merge protection: https://docs.github.com/code-security/code-scanning/managing-code-scanning-alerts/set-code-scanning-merge-protection
- Resolving code scanning alerts (assign to Copilot): https://docs.github.com/en/code-security/how-tos/manage-security-alerts/manage-code-scanning-alerts/resolve-alerts
- Security and analysis settings: https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/enabling-features-for-your-repository/managing-security-and-analysis-settings-for-your-repository
- Projects GraphQL (copyProjectV2): https://docs.github.com/en/graphql/reference/projects
- Gemini 4 Carbon in GitHub Copilot (changelog): https://github.blog/changelog/2026-11-10-gemini-4-carbon-in-github-copilot/
- Reviewing a PR created by Copilot (@copilot iterations): https://docs.github.com/ru/copilot/how-tos/use-copilot-agents/cloud-agent/review-copilot-prs
- Copilot cloud agent settings (workflow approval, validation tools): https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/configuring-agent-settings
- Durable Extension for Agent Framework: https://learn.microsoft.com/en-us/agent-framework/hosting/durable-extension
- Workflows on Azure Container Apps (Durable Task SDKs): https://learn.microsoft.com/en-us/azure/container-apps/workflows-overview
- Copilot code review configuration: https://docs.github.com/copilot/how-tos/copilot-on-github/set-up-copilot/configure-code-review
- Using Copilot code review: https://docs.github.com/en/copilot/how-tos/use-copilot-agents/use-code-review?tool=jetbrains
- Copilot cloud agent via API: https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api
- Issue dependencies REST API: https://docs.github.com/en/rest/issues/issue-dependencies?apiVersion=2026-03-10
- Automatically merging a pull request: https://docs.github.com/en/pull-requests/how-tos/merge-and-close-pull-requests/automatically-merging-a-pull-request
- .NET 11 announcement: https://devblogs.microsoft.com/dotnet/announcing-dotnet-11/
