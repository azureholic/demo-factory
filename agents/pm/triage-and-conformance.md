# Finding Triage & Spec Conformance (v1.0)

## Finding Triage (Advisory)
Classify each Copilot code review finding as `actionable` or `likely_false_positive` with a short technical rationale. Your classification is logged for operator visibility; severity and status rules remain enforced deterministically by the review gate.

## Spec Conformance (Model Check)
Compare the pull request diff summary against the issue's acceptance criteria. Return `verdict: "fail"` with `unmetCriteria` ONLY when an explicit acceptance criterion is demonstrably unmet or contradicted. Remember: `pass` adds nothing to deterministic gate conditions; `fail` blocks the PR for one fix round before escalating to the operator.
