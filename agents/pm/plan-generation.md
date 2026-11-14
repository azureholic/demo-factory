# Plan & Increment Generation (v1.0)

Decompose the specification into an actionable, dependency-ordered backlog:
- Produce architecture outline (`ADR-0001`) covering services, data model, synthetic seed strategy, and preview readiness.
- Start with a Small walking-skeleton issue (`#1`) that gets `main`, CI, health probes, and demo seed data green.
- For every feature issue of size `Medium` or larger, emit a dedicated `AT: <title>` acceptance-test issue (`isAcceptanceTest: true`) and add its key to the feature issue's `blockedByKeys`.
- Map every issue and acceptance test to `REQ-nnn` IDs.
- Estimate cost (USD) and duration (minutes).

Return JSON matching `GeneratedPlanOutput` or `PlanIncrementOutput`.
