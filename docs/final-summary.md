# Final Engineering Summary

## Plan and rationale
The assignment weights "effectiveness of agentic orchestration" above the
product itself, so the plan was to build a small but *real* orchestration
engine first, not a diagram of one, and then use it to produce the URL
shortener. That way every deliverable (code, tests, docs, audit log, metrics)
comes out of the same system being evaluated, rather than being written by
hand and described as if a pipeline had made it.

Concretely, it is a wave-based DAG scheduler in C#. Every stage passes
through `entry gate → human approval (high-impact stages) → execution with
bounded retries → fallback → exit gate`. Policy findings (Pass/Warn/Block)
decide whether the run continues, rolls back or safe-stops, and a stage can
trigger a re-plan of upstream work when it learns something new. See
`docs/architecture.md`.

## How "agentic" is defined here
Each stage acts as an agent with a bounded remit and real inputs:
- Requirements analyses the existing codebase (brownfield).
- Testing runs the test suite and decides the requirement is untestable
  (ambiguous).
- Gates judge outputs against policy.
- The engine decides whether to proceed, retry, fall back, roll back,
  re-plan or stop.

Humans own the two high-impact decisions (writing code, shipping it) and
final quality control.

The agents are **deterministic C#, not LLM calls**. That's a deliberate
choice for a governed prototype: every decision is reproducible,
unit-testable and explainable from the audit log. The extension point is
clear. An LLM-backed stage would implement the same `Execute` contract, and
`CodebaseAnalyzer` returns an `ImpactReport` that an LLM reviewer could
produce instead. The gates, approvals and audit would govern it unchanged,
which is the property that matters when agent output is non-deterministic.

## Artifacts produced
- `orchestrator/`: the engine (`PipelineEngine`, `Stages`, `Policies`,
  `CodebaseAnalyzer`, `LineDiff`, `TestRunner`, `AuditLogger`, `Metrics`,
  `ConsoleApprover`, `ApiTemplates`)
- `orchestrator.Tests/`: 32 tests for the engine, policies, analyzer, diff and TRX parsing
- `baseline/UrlShortener.Api/`: frozen v1 used as the brownfield input
- `generated/UrlShortener.Api/`: the URL shortener minimal API, written by the pipeline
- `generated/UrlShortener.Tests/`: 16 test cases (unit, concurrency, HTTP
  integration), written and **executed** by the pipeline
- Generated per run:
  - `docs/requirements.md`, `docs/design.md`, `docs/release-notes.md`
    (approvals, test evidence, policy results, SHA-256 manifest)
  - `docs/brownfield/impact-analysis.md` and `docs/brownfield/changes.diff`
- `audit.log`, `metrics.json`: evidence of orchestration behaviour
- `UrlShortenerAgentic.sln`: `dotnet test` runs both test suites (48 tests, all passing)

## Risks, trade-offs, validation
See `docs/risks-and-validation.md` for the full risk tables and the policy
catalogue. Headlines:
- The product's concurrency and redirect-scheme defects are fixed and proven
  by runtime tests.
- The accepted v1 risks are in-memory persistence, no auth and no rate limiting.
- The main orchestration trade-off is deterministic agents and heuristic
  (regex) policy scans, chosen for explainability over breadth.

Validation is layered:
- unit tests on every control-flow path
- real test execution feeding release policy
- end-to-end runs of all three scenarios plus a fault-injection run, with
  observed results in `docs/scenarios.md`

## Assumptions
- Single-instance deployment; no distributed coordination needed for
  short-code generation.
- No target scale or SLA was given, so correctness and governance were
  prioritised over performance tuning.
- The reviewer for approval gates is the person running the pipeline,
  identified by `--reviewer` or the OS user name.
- The ambiguous scenario's stakeholder answer is scripted, which stands in
  for a real clarification channel.

## Limitations
- Agents are deterministic (see above). No LLM is in the loop.
- Approvals are console prompts with a self-declared identity, not an
  authenticated review workflow.
- Policies are an explicit rule set with regex-based scans. They catch the
  patterns they describe, not arbitrary vulnerabilities, and aren't a
  substitute for SAST or dependency scanning.
- The brownfield analyzer is rule-based (4 rules). It shows the
  analyse → scope → patch → verify loop end to end, but it is not a general
  code-understanding engine.
- Only the Testing stage has a fallback; other stages retry and then safe-stop.
- The API uses in-memory persistence, has no authentication, and has no
  rate limiting.
- The pipeline state lives in memory for one run. A crashed run restarts
  from the beginning; it does not resume.
