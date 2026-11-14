# Plan & Increment Generation (v1.0)

Decompose the specification into an actionable, dependency-ordered backlog:
- Produce architecture outline (`adr0001Markdown`) covering services, data model, synthetic seed strategy, and preview readiness.
- Start with a `Small` walking-skeleton issue (`ISS-1`) that gets `main`, CI, health probes, and demo seed data green (`initialStatus: "Ready"`).
- Ignore out-of-scope disclaimers and answered clarification Q&A metadata when extracting requirements; extract only concrete in-scope functional/non-functional `REQ-nnn` requirements.
- For every feature issue of size `Medium` or larger, emit a dedicated `AT: <title>` acceptance-test issue (`isAcceptanceTest: true`) and add its key to the feature issue's `blockedByKeys`.
- Map every issue and acceptance test to `REQ-nnn` IDs.
- Estimate cost (USD) and duration (minutes).

For full project plan generation, return strictly valid JSON with this exact structure:
```json
{
  "adr0001Markdown": "# ADR-0001: Architecture Overview\n...",
  "requirements": [
    { "id": "REQ-001", "text": "Testable requirement statement" }
  ],
  "milestones": ["M1: Walking Skeleton", "M2: Core Capabilities"],
  "items": [
    {
      "key": "ISS-1",
      "title": "Walking skeleton: ...",
      "description": "Detailed implementation scope...",
      "acceptanceCriteria": ["Criterion 1", "Criterion 2"],
      "requirementIds": ["REQ-001"],
      "size": "Small",
      "priority": "Critical",
      "milestone": "M1: Walking Skeleton",
      "isAcceptanceTest": false,
      "blockedByKeys": [],
      "blockedByExistingIssueNumbers": [],
      "predictedTouchPaths": ["src/"],
      "initialStatus": "Ready"
    }
  ],
  "estimatedCostUsd": 4.5,
  "estimatedDurationMinutes": 75
}
```
Allowed enum values:
- `size`: `"Small"` | `"Medium"` | `"Large"` | `"XLarge"`
- `priority`: `"Low"` | `"Medium"` | `"High"` | `"Critical"`
- `initialStatus`: `"Ready"` | `"Backlog"`

For feature increment generation, return strictly valid JSON with keys `newRequirements`, `items`, and `overlapWarnings`.

