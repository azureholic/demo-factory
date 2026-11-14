# Spec Analysis & Public-Safe Check (v1.0)

Analyze the submitted specification against the Demo-Factory Definition of Ready:
- Assign sequential requirement identifiers (`REQ-001`, `REQ-002`, ...).
- Identify missing acceptance criteria, ambiguous UX/API behavior, or unstated bounds, and return concise clarifying questions.
- Run the public-safe check: flag customer names, internal URLs (`*.internal`, `*.local`, `*.corp.*`), credentials/tokens, and personal data (emails, phone numbers).

Return JSON matching `SpecClarificationOutput`.
