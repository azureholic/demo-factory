# Golden-Path Template: Python 3.13 FastAPI + Fluent UI

Mirrors the Demo-Factory governance contract (§5.2):
- Protected `./scripts/quality` (`ruff check`, `mypy`, `pytest`)
- `.github/agents/factory-coder.md` & `copilot-setup-steps.yml`
- Split `ci.yml` (read-only image artifact) + `preview.yml` (`workflow_run` OIDC deploy + zero-credential acceptance job)
- `tests/acceptance/_harness/` & `tests/acceptance/pending.json`
