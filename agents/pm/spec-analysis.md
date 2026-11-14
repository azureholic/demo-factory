# Spec Analysis, Definition of Ready & Public-Safe Check (v1.1)

When analyzing a **project specification** (`SpecClarificationOutput`):
- Assign sequential requirement identifiers (`REQ-001`, `REQ-002`, ...).
- Identify missing acceptance criteria, ambiguous UX/API behavior, or unstated bounds, and return concise clarifying questions.
- Run the public-safe check: flag customer names, internal URLs (`*.internal`, `*.local`, `*.corp.*`), credentials/tokens, and personal data (emails, phone numbers).

When reviewing a **single issue against Definition of Ready** (`SpecReviewOutput`):
- The repository already follows an opinionated Demo-Factory golden-path template documented in `AGENTS.md` and `docs/spec.md` (`.NET 11` + `React 19 + Vite` for `dotnet-react`, `Node + TypeScript` for `node-ts`, `Python + FastAPI` for `python-fastapi`, `Vite + TypeScript` for `static-web`, GitHub Actions CI workflows `build`/`test`/`lint`, `GET /healthz` returning `200 OK` JSON `{"status":"healthy"}`, and synthetic in-memory seed data).
- Treat those golden-path conventions and any `"Operator Clarification"` section in `bodyMarkdown` as **already decided** — do **not** ask clarifying questions about framework versions, directory structure, health-probe JSON shape, CI workflow names, or seed-data storage unless the issue explicitly contradicts itself.
- Return `"verdict": "Ready"` (with `"missing": []` and `"questions": []`) whenever the issue has a clear goal and at least one concrete acceptance criterion.
- Return `"verdict": "NeedsClarification"` **only** when the issue body is genuinely empty, contradictory, or missing any testable acceptance criteria.

