# Agentic URL Shortener — Setup & Usage

**Deliverables map:** `docs/architecture.md` (architecture overview) ·
`docs/scenarios.md` (the three required scenarios, with observed runs) ·
`docs/risks-and-validation.md` (risks, guardrails, validation) ·
`docs/final-summary.md` (final engineering summary) · this file (setup +
testing approach).

## Repository layout
| Path | What it is |
|------|------------|
| `orchestrator/` | The agentic SDLC engine: DAG scheduler, gates, policies, approvals, audit, metrics, stages |
| `orchestrator.Tests/` | Unit tests for the engine, policies, codebase analyzer, diff and test-result parsing |
| `baseline/UrlShortener.Api/` | Frozen v1 of the service with real defects: the input to the brownfield scenario |
| `generated/` | The URL shortener API + its tests, **written by the pipeline** |
| `docs/` | Hand-written deliverables plus pipeline-generated `requirements.md`, `design.md`, `release-notes.md`, `brownfield/` |
| `audit.log`, `metrics.json` | Append-only audit trail and metrics for the latest run(s) |

## Prerequisites
- .NET 8 SDK (`dotnet --version` should show 8.x)

## Run the pipeline
From the repository root (the orchestrator finds the root itself, so it can
also be run from any subfolder):

```bash
dotnet run --project orchestrator -- --scenario=greenfield
dotnet run --project orchestrator -- --scenario=brownfield
dotnet run --project orchestrator -- --scenario=ambiguous
```

Options:
- `--reviewer=<name>`: the reviewer identity recorded on approvals (default: OS user name).
- `--inject-fault=test-runner`: makes the test runner fail, to show retries, the
  degraded fallback, and the release policy refusing unverified evidence.

You approve two high-impact stages, `implementation` and `release` (type
`y` + Enter). Before each prompt, the policy warnings you are accepting are
shown. The process exits with code 0 when the pipeline completes, and 1 on a
safe-stop, so CI can gate on it.

What a greenfield run does (the audit log shows every step):
1. Requirements → Design → Implementation (approval) write real artifacts,
   and each output passes an exit gate.
2. `testing` and `docs` run **in parallel**. Testing runs the real
   `dotnet test` on the generated project and records per-test results.
3. The first pass generates a 1-test suite. The release **entry gate**
   (policy CHG-002) finds fewer than 2 tests and **rolls back** to `testing`,
   which regenerates the full suite (16 test cases) and runs it.
4. The release gate then checks change control (CHG-001), passing tests
   (CHG-002) and that the security control is verified by tests (SEC-002).
   You approve, and the release notes are written with an artifact manifest
   (SHA-256) that the release exit gate verifies.

After a run, look at:
- `audit.log`: JSONL, one event per line (gates and policy results, approvals
  with reviewer, retries, fallbacks, rollbacks, re-plans, every context decision)
- `metrics.json`: success rate, retries, rollbacks, fallbacks, incidents,
  MTTR, and wall-clock vs. automated latency
- `docs/release-notes.md`: approvals, test evidence, policy results, manifest
- `docs/brownfield/`: impact analysis and patch (after a brownfield run)

## Build / run / test
```bash
dotnet test UrlShortenerAgentic.sln          # orchestrator tests + generated API tests

dotnet run --project generated/UrlShortener.Api
# in another terminal (use the port printed at startup):
curl -X POST http://localhost:5000/shorten -H "Content-Type: application/json" -d '{"url":"https://example.com"}'
```

## Testing approach
- **Generated API (`generated/UrlShortener.Tests`, 16 test cases):**
  - Unit tests on the store: URL validation, rejection of non-http(s) schemes
    (a theory with 4 cases), click tracking, and two concurrency tests
    (5,000 parallel clicks lose no updates; 10,000 parallel creates issue unique codes).
  - `WebApplicationFactory` integration tests that do real HTTP round-trips
    through `POST /shorten` → `GET /{code}` (302 + `Location`) →
    `GET /analytics/{code}`, including the 400 and 404 paths and a
    `javascript:` URL being rejected.
- **Orchestrator (`orchestrator.Tests`, 32 tests):**
  - Engine behaviour with in-memory stages: parallel waves and
    synchronisation; approval rejection → safe-stop; reviewer and accepted
    warnings recorded; retry → resolved incident / MTTR; retries exhausted →
    fallback (degraded) or → safe-stop; entry and exit gate blocks; a gate
    that throws fails closed; gate rollback and recovery; re-plan re-running
    succeeded stages without losing a slow sibling's reset; decisions written
    to the audit log.
  - Policies (secret scan, PII scan, test evidence, stale design, scope
    containment), the brownfield analyzer against the real baseline, the diff,
    and TRX parsing.
- **End-to-end:** all three scenarios plus the fault-injection run were
  executed, and their audit excerpts and metrics are in `docs/scenarios.md`.

## Known limitations / trade-offs
- Stage "agents" are deterministic C# rather than LLM calls. Every decision
  they make comes from real inputs: code analysis, test results, policy
  evaluation. See `docs/final-summary.md` for the rationale and the extension point.
- In-memory persistence only, so there is no durability across restarts.
- No authentication or rate limiting on the API (documented as accepted v1 risks).
- Approvals are console prompts with a self-declared reviewer name, not an
  authenticated review system.
- Policies are an explicit, small rule set (regex scans plus evidence checks),
  not a general policy engine.
