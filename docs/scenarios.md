# Scenario Walkthroughs

All three scenarios run the same engine and stage graph. The `--scenario`
flag only changes what individual stages do with their inputs, which shows
the orchestration layer is generic infrastructure rather than per-scenario
glue code. A fourth run shows the failure path with an injected fault.

Every excerpt below is from `audit.log` for real runs on 2026-09-23. Timestamps
are trimmed, and `start`, `decision` and passing `policy` lines are omitted
for length (the full log keeps them). Numbers come from `metrics.json`.
Approvals were entered by the person running the pipeline.

---

## 1. Greenfield — `dotnet run --project orchestrator -- --scenario=greenfield`

**Decomposition**
- Requirement: "build a URL shortener from scratch."
- `requirements.md` normalises it into scope, **testable acceptance
  criteria** (status codes, scheme allow-list, no lost clicks or duplicate
  codes under concurrency), decisions and assumptions. It records what is
  *excluded* (custom aliases) and what data is *not* collected (no PII,
  enforced by CMP-001).
- The tasks are the stage graph: requirements → design → implementation →
  {testing ∥ docs} → release, each with an exit gate on its output.

**Orchestration**
- The implementation and release approval gates fire. The release entry
  gate is evaluated *before* the approval prompt, so the reviewer is never
  asked to approve something policy would reject anyway.
- `testing` runs the real `dotnet test`. The first pass uses a minimal
  1-test suite, CHG-002 rolls back to `testing`, and the full suite runs.

**Validation**
- Implementation exit gate: SEC-001 (no secrets) and CMP-001 (no PII fields).
- Release entry gate: CHG-001 (built from the latest approved design),
  CHG-002 (≥2 tests, all passing) and SEC-002 (the scheme allow-list is
  verified by passing unit and HTTP tests).
- Release exit gate: REL-001 verifies the SHA-256 artifact manifest recorded
  in `release-notes.md`.

**Observed run**
```
implementation  approval            approved by dines
implementation  succeeded           UrlShortener.Api source generated (Program.cs, Models.cs, Store.cs, csproj)
docs            succeeded           API README.md written
testing         succeeded           executed 1 test(s) via dotnet test (minimal suite): 1 passed, 0 failed
release         guardrail-rollback  target=testing gate=entry reason=CHG-002 WARN: only 1 test(s) present, minimum is 2 - rolling back to testing for more coverage
testing         rollback            rolled back to testing
release         rollback            rolled back to testing
testing         succeeded           executed 16 test(s) via dotnet test (full suite): 16 passed, 0 failed
release         policy              entry-gate CHG-001 PASS: implementation was approved and built against design revision 1 (the latest)
release         policy              entry-gate CHG-002 PASS: 16 tests executed, all passing
release         policy              entry-gate SEC-002 PASS: redirect scheme allow-list verified by passing tests
release         approval            approved by dines
release         policy              exit-gate REL-001 PASS: artifact manifest verified (8 files unchanged since release notes were written)
release         succeeded           release notes written with 8-file manifest; build marked release-ready
pipeline        complete            all stages resolved
```
Outcome `complete`, 6/6 stages, 0 retries, 1 rollback, 1 incident (the
coverage rollback) resolved with **MTTR ≈ 3.4s** (the time to regenerate and
run the full suite), 2 approvals, **≈ 7.4s wall clock**, almost all of it in
`dotnet test`.

---

## 2. Brownfield — `dotnet run --project orchestrator -- --scenario=brownfield`

**Decomposition and codebase reasoning**
- Requirement: "improve reliability" of the **existing** service in
  `baseline/UrlShortener.Api` (a checked-in v1 with real defects).
- Before anything is decided, `Requirements` runs `CodebaseAnalyzer` over the
  baseline and writes `docs/brownfield/impact-analysis.md`:
  - **API surface and data flow:** 3 endpoints, each traced to the
    `IUrlStore` calls it makes (e.g. `GET /{code}` → `Get` → `RecordClick`).
  - **Findings with evidence:**
    - BF-001 shared `Random` (`Store.cs:19`)
    - BF-002 `ContainsKey`-then-assign race (`Store.cs:31`)
    - BF-003 unsynchronised `ClickCount++` (`Store.cs:44`)
    - BF-004 no scheme allow-list, so `javascript:` URLs become redirect
      targets (`Store.cs:24`)
  - **Change scope:** impacted `Store.cs`; not impacted `Program.cs`,
    `Models.cs`. Because every endpoint goes through `IUrlStore`, fixing the
    store leaves the public API contract untouched.
- `Design` turns this into a change plan: change only the impacted files,
  and prove regression safety with HTTP tests plus re-analysis.

**Orchestration**
- `Implementation` checks the baseline out into `generated/`, writes only
  the files whose content changes, and records the patch as
  `docs/brownfield/changes.diff` (one hunk, `Store.cs` only).
- Its exit gate enforces **scope** (CHG-004: changed files ⊆ impacted files)
  and **verifies the fix** (CHG-005: re-running the analyzer on the patched
  code finds none of BF-001…004).

**Validation**
- Same release bar as greenfield: brownfield changes don't get a lower bar.
- The HTTP integration tests act as the regression suite for the existing
  endpoints, and the concurrency tests prove BF-002 and BF-003 are fixed at
  runtime, not only in static analysis.

**Observed run**
```
requirements    policy              exit-gate REQ-002 PASS: impact analysis identified the change scope: Store.cs
requirements    succeeded           requirements.md + impact-analysis.md written
implementation  approval            approved by dines
implementation  policy              exit-gate CHG-004 PASS: changed files (Store.cs) are within the analysed impact scope (Store.cs)
implementation  policy              exit-gate CHG-005 PASS: re-analysis of the changed code clears all baseline findings (BF-001, BF-002, BF-003, BF-004)
implementation  succeeded           patched baseline: changed Store.cs; unchanged UrlShortener.Api.csproj, Models.cs, Program.cs; patch in docs/brownfield/changes.diff
testing         succeeded           executed 1 test(s) via dotnet test (minimal suite): 1 passed, 0 failed
release         guardrail-rollback  target=testing gate=entry reason=CHG-002 WARN: only 1 test(s) present, minimum is 2 - rolling back to testing for more coverage
testing         succeeded           executed 16 test(s) via dotnet test (full suite): 16 passed, 0 failed
release         approval            approved by dines
release         succeeded           release notes written with 8-file manifest; build marked release-ready
pipeline        complete            all stages resolved
```
Outcome `complete`, 6/6 stages, 1 rollback, MTTR ≈ 3.6s, 2 approvals,
≈ 8.0s wall clock. This run's answers were piped from Windows PowerShell,
which adds a byte-order mark to the first line. The approver ignores it, and
anything other than `y` still rejects.

---

## 3. Ambiguous — `dotnet run --project orchestrator -- --scenario=ambiguous`

**Decomposition**
- Requirement as given: "make the URL shortener better", with no target,
  scale or auth model.
- `requirements.md` lists the three ambiguities and a provisional
  interpretation, and marks the acceptance criterion for "better" as
  **OPEN** instead of silently inventing one.

**Orchestration — dynamic re-planning**
- The clarification arrives *after* code has been generated. On its first
  pass, `Testing` can't write a test for an open criterion. It records the
  question and the stakeholder's answer ("better" = reliability) and returns
  `ReplanFrom = "design"`.
- The engine resets `design` and everything downstream (including stages
  that already succeeded), logging one `replan` entry per stage. They re-run:
  - `design.md` becomes revision 2 with a "Clarifications applied" section.
  - `implementation` goes back through its **approval gate**, because code
    built on a changed design is reviewed again.
  - At release, CHG-001 confirms the implementation matches design revision 2.
- `docs` was still running when the re-plan was requested. Resets are
  applied only once the wave finishes, so `docs succeeded` appears before
  the `replan` lines and `docs` still re-runs.

**Observed run**
```
design          succeeded           design.md written (revision 1)
implementation  approval            approved by dines
implementation  succeeded           UrlShortener.Api source generated (Program.cs, Models.cs, Store.cs, csproj)
testing         succeeded           requirement untestable as written; stakeholder clarified 'better' = reliability - re-planning from design
docs            succeeded           API README.md written
design          replan              reset: design invalidated by clarification raised in testing
implementation  replan              reset: design invalidated by clarification raised in testing
testing         replan              reset: design invalidated by clarification raised in testing
docs            replan              reset: design invalidated by clarification raised in testing
release         replan              reset: design invalidated by clarification raised in testing
design          succeeded           design.md revised with clarification (revision 2)
implementation  approval            approved by dines
implementation  succeeded           UrlShortener.Api source generated (Program.cs, Models.cs, Store.cs, csproj)
docs            succeeded           API README.md written
testing         succeeded           executed 1 test(s) via dotnet test (minimal suite): 1 passed, 0 failed
release         guardrail-rollback  target=testing gate=entry reason=CHG-002 WARN: only 1 test(s) present, minimum is 2 - rolling back to testing for more coverage
testing         succeeded           executed 16 test(s) via dotnet test (full suite): 16 passed, 0 failed
release         approval            approved by dines
release         succeeded           release notes written with 8-file manifest; build marked release-ready
pipeline        complete            all stages resolved
```

| Stage          | Runs | Rollbacks | Why |
|----------------|------|-----------|-----|
| requirements   | 1    | 0 | upstream of the re-plan |
| design         | 2    | 1 | re-planned to revision 2 |
| implementation | 2    | 1 | re-planned; re-approved |
| testing        | 3    | 2 | clarification pass, minimal suite, full suite |
| docs           | 2    | 1 | re-planned |
| release        | 1    | 0 | reset while still pending; rolled back at its entry gate without running |

Outcome `complete`, 5 rollbacks, 3 approvals, MTTR ≈ 4.0s, ≈ 10.1s wall clock.

---

## 4. Failure path — `--scenario=greenfield --inject-fault=test-runner`

Shows bounded retries, fallback and fail-closed policy together.

```
testing         exception           attempt=1 error=injected fault: test runner unavailable
testing         exception           attempt=2 error=injected fault: test runner unavailable
testing         exception           attempt=3 error=injected fault: test runner unavailable
testing         fallback            primary path exhausted retries (injected fault: test runner unavailable); running degraded fallback
testing         succeeded           inventoried 1 test(s) without executing them [DEGRADED: via fallback]
release         guardrail-rollback  target=testing gate=entry reason=CHG-002 WARN: only 1 test(s) present, minimum is 2 - rolling back to testing for more coverage
  ... (testing retries and falls back again, inventorying 13 test methods) ...
release         policy              entry-gate CHG-003 WARN: test evidence is degraded: static inventory (tests not executed)
release         policy              entry-gate SEC-002 BLOCK: security control not verified by an executed, passing test: Create_RejectsNonHttpSchemes, Shorten_NonHttpScheme_Returns400
release         gate-blocked        entry gate: SEC-002 BLOCK: security control not verified by an executed, passing test: ...
pipeline        safe-stop           halted: a stage was rejected, blocked by policy, or failed without recovery
```
Outcome `safe-stop`, exit code 1:
- 5/6 stages succeeded (83.3%), 4 retries, 2 fallbacks.
- Incidents: 2 resolved (testing, both times via the fallback, MTTR ≈ 0.6s)
  and **1 left open** (release). The open incident is the signal that a
  human needs to act.
- The fallback kept the pipeline producing evidence, and policy still
  refused to ship a security control that no executed test had verified.
- The release approval was never requested, because the block came first.

(The static inventory counts 13 test *methods*; an executed run reports 16
*results*, because the scheme-rejection theory runs 4 cases.)
