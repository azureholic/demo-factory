# Azure Deployment Plan

> **Status:** Ready for Validation

Generated: 2026-11-14T10:38:00+01:00
Updated: 2026-11-14T11:40:00+01:00

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
| **Subscription** | Pending user confirmation (local implementation vs. live Azure subscription) |
| **Location** | `swedencentral` (EU region for Foundry data residency; pending user confirmation) |

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

---

## 7. Validation Proof (Local Build, Test & IaC)

| Check | Command Run | Result |
|-------|-------------|--------|
| .NET 11 Solution Build & Tests | `dotnet test DemoFactory.slnx` | ✅ Pass (18/18 tests: 10 Domain + FsCheck property, 4 Orchestrator Replay & MAF, 4 E2E) |
| Web Frontend TypeScript & Vite Bundle | `npm run build && npm test` | ✅ Pass (0 TS errors, Vitest passed) |
| Azure Bicep Infrastructure Lint & Compile | `az bicep build --file infra/main.bicep` | ✅ Pass (0 errors, 0 BCP warnings, Entra-only SQL & Storage) |
| Live UI & SignalR Verification | `DemoFactory.Api` + Integrated Browser | ✅ Pass (Overview, Board, Work Item 15-condition Gate, Issue Inbox, Theme toggle) |
