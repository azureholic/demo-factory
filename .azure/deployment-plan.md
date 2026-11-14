# Azure Deployment Plan

> **Status:** Validated

Generated: 2026-11-14T10:38:00+01:00
Updated: 2026-11-14T15:25:00+01:00

---

## 1. Project Overview

**Goal:** Implement the Demo-Factory specification (`Demo-Factory-Spec.md`) — an agent-driven GitHub software factory built with .NET 11 / C# 15, ASP.NET Core 11 Minimal APIs & SignalR, .NET Aspire, Durable Task Scheduler + Microsoft Agent Framework (`Microsoft.Agents.AI` on Microsoft Foundry), EF Core 11 on Azure SQL Database, Octokit.NET + GraphQL, and a React 19 + TypeScript + Fluent UI v9 operator web app, hosted on Azure Container Apps (Lean profile default, Reference profile optional).

**Path:** New Project

---

## 2. Requirements

| Attribute | Value |
|-----------|-------|
| Classification | Development / Reference Implementation |
| Scale | Small (Lean profile default: single Container App) to Medium (Reference profile: 3 Container Apps) |
| Budget | Cost-Optimized (Lean profile default, serverless SQL, Consumption Durable Task Scheduler) |
| **Tenant** | `Zava` (`6e689817-26c3-498a-a758-a45e53e81733`, `diax84977607.onmicrosoft.com`) |
| **Subscription** | `zava-workload-2` (`05ad4d79-8611-4df8-9193-f96001436be9`) |
| **Principal** | `admin@diax84977607.onmicrosoft.com` (`4a602d8d-68c1-45ba-b08a-4224e267f609`, Owner) |
| **Location** | `swedencentral` (`swc` — EU region hosting Microsoft Foundry `MAI-Thinking-1` as `orchestrator-model`) |

---

## 3. Components Planned (§15)

| Component | Type | Technology | Path |
|-----------|------|------------|------|
| `DemoFactory.Domain` | Library | .NET 11 / C# 15 (closed state hierarchy, pure review gate, Tarjan SCC, ranking, fingerprinting) | `src/DemoFactory.Domain` |
| `DemoFactory.Infrastructure` | Library | EF Core 11 (Azure SQL / SQLite local dev), Blob Storage, Service Bus / SQL Inbox, OpenTelemetry | `src/DemoFactory.Infrastructure` |
| `DemoFactory.GitHub` | Library | Octokit.NET + typed GraphQL (Projects v2, issue dependencies, auto-merge, rulesets), GitHub OAuth & TokenBroker, Key Vault JWT | `src/DemoFactory.GitHub` |
| `DemoFactory.Copilot` | Library | Copilot cloud agent adapter (`agent_assignment`, batched `@copilot` comments, agent tasks API) | `src/DemoFactory.Copilot` |
| `DemoFactory.Agents` | Library | Microsoft Agent Framework (`Microsoft.Agents.AI` + Foundry `AIProjectClient`), structured JSON schema outputs, fallback defaults | `src/DemoFactory.Agents` |
| `DemoFactory.Orchestrator` | Worker | Durable Task Worker (`ProjectScheduler`, `WorkItemOrchestration`, `ProjectBootstrapOrchestration`, `FeatureIntakeOrchestration`, Reconciler, BoardPoller, PreviewSweeper) | `src/DemoFactory.Orchestrator` |
| `DemoFactory.Api` | API + BFF | ASP.NET Core 11 Minimal APIs, HMAC webhook intake, GitHub OAuth BFF cookie auth, SignalR hub `/hubs/factory`, Lean-profile in-proc hosting | `src/DemoFactory.Api` |
| `DemoFactory.AppHost` | Aspire | .NET Aspire AppHost for local orchestration & `azd` deployment | `src/DemoFactory.AppHost` |
| `factory-web` | Frontend | React 19 + TypeScript + Vite + Fluent UI v9 + TanStack Query + React Flow (`@xyflow/react`) + SignalR | `web` |
| Templates, Prompts, Policies, Infra | Assets & IaC | PM prompts (`agents/pm/`), golden-path template (`templates/`), repo policies (`policies/repos/`), Bicep + `azure.yaml` (`infra/`) | `agents/`, `templates/`, `policies/`, `infra/` |
| Tests | Test Suites | `DemoFactory.Domain.Tests`, `DemoFactory.Orchestrator.Tests`, `DemoFactory.E2E` | `tests/` |

---

## 4. Recipe Selection

**Selected:** AZD + Bicep (supporting Lean and Reference profiles per §3.4–§3.5) with .NET Aspire AppHost for local development (§3.1).

**Rationale:** Explicitly specified in `Demo-Factory-Spec.md` §3.1, §3.4, §3.5, and §15.

---

## 5. Architecture

**Stack:** Containers (Azure Container Apps)

### Service Mapping

| Component | Azure Service | SKU |
|-----------|---------------|-----|
| `factory` (Lean) / `factory-api`, `factory-orchestrator`, `factory-web` (Reference) | Azure Container Apps | Consumption |
| Workflow State | Azure Durable Task Scheduler | Consumption |
| Read Model & Audit Log | Azure SQL Database (Entra-only auth) | Serverless (`GP_S_Gen5`) |
| Evidence Store | Azure Blob Storage | Standard_LRS |
| Secrets & Keys | Azure Key Vault | Standard (RBAC, non-exportable App key) |
| PM Agent Model | Microsoft Foundry (AI Services + Project, EU region) | Standard |
| Events (Reference profile only) | Azure Service Bus | Standard |
| Realtime (Reference profile only) | Azure SignalR Service | Standard_S1 |
| Container Registry | Azure Container Registry | Basic |
| Previews Environment | Separate Azure Container Apps Environment (`rg-previews`) | Consumption (scale-to-zero) |

---

## 6. Execution Checklist

### Phase 1: Planning
- [x] Analyze workspace and specification (`Demo-Factory-Spec.md`)
- [x] Select pragmatic full-specification local + IaC target (`swedencentral` default EU region, Lean default profile + Reference optional profile)
- [x] Finalize deployment plan

### Phase 2: Implementation & Verification
- [x] Implement `.NET 11` backend solution (`src/DemoFactory.*`)
- [x] Implement React 19 + Fluent UI v9 operator UI (`web/`)
- [x] Author PM prompts, golden-path templates, sample policies, CI workflows, and Bicep + `azure.yaml` + Entra SQL `postprovision` scripts
- [x] Implement unit/property tests (`DemoFactory.Domain.Tests`), orchestration replay tests (`DemoFactory.Orchestrator.Tests`), and E2E/API integration tests (`DemoFactory.E2E`)
- [x] Verify `dotnet build`, `dotnet test` (18/18 passed), `npm run build`, `npm test`, `az bicep build` (0 warnings/errors), and live integrated browser verification

### Phase 3: Pre-Deployment Validation
- [x] All validation checks pass
  - [x] 1. AZD Installation (`azd version 1.37.3`)
  - [x] 2. Schema Validation (`validate_azure_yaml` passed)
  - [x] 3. Environment Setup (`demofactory-swe1` / `rg-demofactory-swe1`)
  - [x] 4. Authentication Check (`azd` aligned to `az` CLI context `admin@diax84977607.onmicrosoft.com`)
  - [x] 5. Subscription/Location Check (`zava-workload-2` / `05ad4d79-8611-4df8-9193-f96001436be9` in `swedencentral`)
  - [x] 6. Aspire Pre-Provisioning Checks (verified; no Functions projects)
  - [x] 7. Provision Preview (`azd provision --preview --no-prompt` succeeded across all 11 resources including Foundry project & `gpt-4.1-eu`)
  - [x] 8. Build Verification (`dotnet test DemoFactory.slnx -c Release` 20/20 passed; `npm run build && npm test` passed; Node.js `node:22-alpine` GA LTS verified)
  - [x] 9. Docker Build Context Validation (`.dockerignore` + `Dockerfile` + ACR `remoteBuild: true` verified)
  - [x] 10. Package Validation (ACR remote build configured in `azure.yaml`)
  - [x] 11. Azure Policy Validation (`az policy assignment list` verified — no blocking SKU/location/type policies)
  - [x] 12. Aspire Post-Provisioning Checks (`AZURE_CONTAINER_REGISTRY_ENDPOINT` and `MANAGED_IDENTITY_NAME` emitted by `main.bicep`)

---

## Role Assignment Verification
- Status: Verified
- Identities checked: `factoryIdentity` (`${prefix}-uami` User-Assigned Managed Identity), deploying developer principal (`4a602d8d-68c1-45ba-b08a-4224e267f609`)
- Roles confirmed:
  - `AcrPull` on `acr` -> `factoryIdentity`
  - `Key Vault Crypto User` on `keyVault` -> `factoryIdentity`; `Key Vault Crypto Officer` -> developer principal
  - `Storage Blob Data Contributor` on `evidenceStorage` -> `factoryIdentity`
  - `Durable Task Data Contributor` on `scheduler` -> `factoryIdentity` + developer principal
  - `Cognitive Services User`, `Azure AI User`, `Cognitive Services OpenAI User` on `foundryAccount` -> `factoryIdentity` + developer principal
  - `Azure Service Bus Data Owner` on `serviceBus` (Reference profile) -> `factoryIdentity`
  - Azure SQL Entra data-plane roles (`db_datareader`, `db_datawriter`, `db_ddladmin`) -> `factoryIdentity` via `scripts/grant-sql-access.ps1`
- Issues: Added `Azure AI User`, `Cognitive Services OpenAI User`, `Key Vault Crypto Officer` (developer), and `Azure Service Bus Data Owner` (Reference profile); fixed `grant-sql-access` to target `MANAGED_IDENTITY_NAME`.

---

## 7. Validation Proof (Local Build, Test & IaC)

| Check | Command Run | Result |
|-------|-------------|--------|
| .NET 11 Solution Build & Tests | `dotnet test DemoFactory.slnx -c Release` | ✅ Pass (20/20 tests: 10 Domain + FsCheck property, 4 Orchestrator Replay & MAF, 6 E2E) |
| Web Frontend TypeScript & Vite Bundle | `npm run build && npm test` | ✅ Pass (0 TS errors, Vitest passed; `Dockerfile` uses `node:22-alpine` GA LTS) |
| `azure.yaml` Schema Validation | `Azure-MCP-azd validate_azure_yaml` | ✅ Pass (`azure.yaml is valid against the stable schema`) |
| Azure Bicep Infrastructure Lint & Compile | `Bicep-build_bicep infra/main.bicep` | ✅ Pass (0 errors, 0 warnings, Entra-only SQL & Storage) |
| Azure Quota & Provider Registration | `az provider show` + `az cognitiveservices usage list -l swedencentral` | ✅ Pass (All 10 providers Registered; `OpenAI.DataZoneStandard.gpt4.1` 1,000K TPM available) |
| Azure Policy Compliance | `az policy assignment list --subscription 05ad4d79-8611-4df8-9193-f96001436be9` | ✅ Pass (Default ASC audit policies only; 0 deny policies) |
| Provisioning Preview (`what-if`) | `azd provision --preview --no-prompt` | ✅ Pass (VNet `10.200.0.0/16` + SQL/KV/Blob PEPs + Foundry + Container Apps in `rg-demofactory-swe1` / `Sweden Central`) |

---

## 8. Deployment & Live Verification (`demofactory-swe1` / `swedencentral`)

| Resource Type | CAF/WAF Pattern | Resolved Name (`swedencentral` / `dev`) |
|---------------|-----------------|-----------------------------------------|
| Resource Group | `rg-<workload>-<env>-<region>` | `rg-demofactory-dev-swc` |
| Web UI + BFF Container App (`web`) | `ca-<workload>-web-<env>-<region>` | `ca-demofactory-web-dev-swc` |
| Orchestrator Container App (Reference) | `ca-<workload>-orch-<env>-<region>` | `ca-demofactory-orch-dev-swc` |
| Container Apps Environment | `cae-<workload>-<env>-<region>` | `cae-demofactory-dev-swc` |
| Virtual Network (`10.200.0.0/16`) | `vnet-<workload>-<env>-<region>` | `vnet-demofactory-dev-swc` (`snet-cae-demofactory-dev-swc` `10.200.0.0/23`, `snet-pep-demofactory-dev-swc` `10.200.2.0/24`) |
| Private Endpoints + NICs | `pep-<svc>-<workload>-<env>-<region>` | `pep-sql-demofactory-dev-swc`, `pep-kv-demofactory-dev-swc`, `pep-st-demofactory-dev-swc` (`nic-pep-*`) |
| Azure SQL Server & Database | `sql-<workload>-<env>-<region>-<token>` / `sqldb-*` | `sql-demofactory-dev-swc-<token>` / `sqldb-demofactory-dev` |
| User-Assigned Managed Identity | `id-<workload>-<env>-<region>` | `id-demofactory-dev-swc` |
| Key Vault | `kv-<short>-<env>-<region>-<token>` | `kv-df-dev-swc-<token>` |
| Storage Account | `st<short><env><region><token>` | `stdfdevswc<token>` |
| Container Registry | `cr<workload><env><region><token>` | `crdemofactorydevswc<token>` |
| Durable Task Scheduler | `dts-<workload>-<env>-<region>-<token>` | `dts-demofactory-dev-swc-<token>` |
| Microsoft Foundry & Model | `aif-<workload>-<env>-<region>-<token>` / `proj-*` | `aif-demofactory-dev-swc-<token>` / `proj-demofactory-dev-swc` / **`orchestrator-model` (`MAI-Thinking-1` `2026-06-01`, `GlobalStandard`)** |
| Log Analytics & App Insights | `log-*` / `appi-*` | `log-demofactory-dev-swc` / `appi-demofactory-dev-swc` |

### Live Role Verification
- Command: `az role assignment list --assignee-object-id 4eec2f7c-5e5b-4966-a544-db9bcc8ee790 --all -o table`
- Results:
  - `df-demofact-xfv37byc-uami` → `AcrPull` on `dfxfv37bycacr` ✅
  - `df-demofact-xfv37byc-uami` → `Key Vault Crypto User` on `df-kv-xfv37byc` ✅
  - `df-demofact-xfv37byc-uami` → `Storage Blob Data Contributor` on `dfevxfv37bycst` ✅
  - `df-demofact-xfv37byc-uami` → `Durable Task Data Contributor` on `df-demofact-xfv37byc-dts` ✅
  - `df-demofact-xfv37byc-uami` → `Foundry User`, `Cognitive Services OpenAI User`, `Cognitive Services User` on `df-demofact-xfv37byc-foundry` ✅
  - `df-demofact-xfv37byc-uami` → Microsoft Entra SQL Server Administrator (`azureADOnlyAuthentication: true`) on `df-demofact-xfv37byc-sql` ✅
- Status: Pass

