# Demo-Factory Agent Instructions

Demo-Factory is an autonomous GitHub software factory implemented in **.NET 11 / C# 15**, **ASP.NET Core 11**, **.NET Aspire**, **Durable Task Scheduler**, **Microsoft Agent Framework on Microsoft Foundry**, and **React 19 + Fluent UI v9**.

If you are in VS Code, read the vscode-microsoft-foundry skill first.

## Architectural Guardrails
1. **Agents advise and write target-repo code; deterministic code enforces permissions, state transitions, and the 15-condition review gate (`ReviewGateEvaluator`).** A model check can only block/escalate; it is never the reason a step passes.
2. **Never merge changes to Demo-Factory automatically.** All changes to this repository require human review and approval (§15).
3. **Entra-only Azure authentication.** Never introduce SQL admin passwords or storage account keys in Bicep or application configuration.
4. **Trust boundary.** Only issues carrying the configured work label (`factory`), authored and labelled by the owner or factory, may be scheduled or assigned to `copilot-swe-agent[bot]`.
