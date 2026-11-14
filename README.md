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
