# Demo-Factory

*An agent-driven GitHub software factory for building, and showcasing, demos.*

Implements [`Demo-Factory-Spec.md`](./Demo-Factory-Spec.md) (v0.8):
- **.NET 11 / C# 15** deterministic state machine, Tarjan SCC dependency scheduler, finding fingerprinter, and 15-condition source-pinned review gate (`DemoFactory.Domain`)
- **Durable Task Scheduler + Microsoft Agent Framework (`Microsoft.Agents.AI` on Microsoft Foundry)** for checkpointed orchestrations and schema-validated Project Manager Agent capabilities (`DemoFactory.Agents`, `DemoFactory.Orchestrator`)
- **GitHub App BFF + Serialized `TokenBroker` + Copilot Cloud Agent Adapter** (`DemoFactory.GitHub`, `DemoFactory.Copilot`)
- **ASP.NET Core 11 Minimal APIs + SignalR Realtime Hub + .NET Aspire AppHost** (`DemoFactory.Api`, `DemoFactory.AppHost`)
- **React 19 + TypeScript + Fluent UI v9 + TanStack Query + React Flow** operator UI (`web/`)
- **Azure Container Apps Bicep + `azd`** supporting both **Lean** (single Container App default) and **Reference** deployment profiles (`infra/`, `azure.yaml`)

## Quick Start (Local Development)

```powershell
# 1. Build & run all Domain, Orchestrator Replay, and E2E tests
dotnet test DemoFactory.slnx

# 2. Build & test the React 19 + Fluent UI v9 frontend
cd web
npm install --legacy-peer-deps
npm run build
npm test
cd ..

# 3. Run the API + Orchestrator (Lean profile, http://localhost:5180) and Vite UI (http://localhost:5173)
dotnet run --project src/DemoFactory.Api
```

Or run the full distributed local stack with **.NET Aspire**:
```powershell
dotnet run --project src/DemoFactory.AppHost
```

## PR progress watchdog

The existing 30-second reconciler watches factory work items for real changes to the
head SHA, coding-agent state, check results, and review findings. Polling alone does
not reset its inactivity clock. Observations and recovery reservations are persisted
in the audit store, including across worker restarts.

Repository policy can override these defaults:

```yaml
prWatchdog:
  enabled: true
  noProgressMinutes: 20
  agentNoProgressMinutes: 90
  retryCooldownMinutes: 5
  maxRecoveryActionsPerHead: 2
```

For a stalled trusted PR, the watchdog can cancel a hung Actions run, rerun a
completed run, request a missing Copilot review, update a behind branch, or request
a conflict-resolution fix round. Cancellation and rerun each consume one recovery
action; the default budget permits one cancel/restart cycle per head SHA. Workflow
recovery verifies the run belongs to that PR, its current head, and the same
repository, and matches required job names. Missing jobs/triggers, permission
failures, stalled agent sessions, and exhausted budgets escalate to **Blocked**
with an audit trail and PR comment rather than waiting forever.

Human approval waits and L0 mode receive a single notification instead of automatic
recovery. Paused/stopped repositories are not recovered, and Demo-Factory itself is
notification-only. Recovery never waives checks or approvals; conflict fixes use
the existing round/session budgets and must pass the normal review gate.

Pending CI no longer invokes PM triage/gate evaluation on every check-state change,
and waiting for a requested review no longer starts unnecessary coding fix sessions.
For more throughput, first measure time spent coding, in CI, awaiting review, and
awaiting approval. Both checked-in target-repository policies use WIP 1; raising WIP
to 2 can overlap independent work, with the existing dependency and touch-set
conflict checks, but increases concurrency/cost and is not enabled by this change.
