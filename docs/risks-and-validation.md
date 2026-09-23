# Risks, Trade-offs, and Validation

## Product risks (URL shortener itself)
| Risk | Impact | Mitigation in this prototype | Left open |
|---|---|---|---|
| Short-code collision / race | Two URLs map to one code | Codes reserved atomically with `TryAdd`; bounded retry on collision. Concurrency test: 10,000 parallel creates, all unique | Not distributed-safe: a second instance could issue the same code |
| Lost click updates | Analytics under-count under load | Per-entry lock; readers get snapshots. Concurrency test: 5,000 parallel clicks, none lost | — |
| Malicious redirect targets | `javascript:`/`data:`/`file:` URLs served via redirect (script injection, open redirect) | http/https-with-host allow-list; unit theory (4 schemes) + HTTP test; SEC-002 blocks release unless both pass | No domain deny-list (e.g. known phishing hosts) |
| Data loss on restart | All shortened URLs disappear | None: in-memory only | Swap `IUrlStore` for EF Core/SQL before production |
| No auth on analytics endpoint | Anyone with a code can read its click count | None | Accepted v1 assumption in `requirements.md` |
| Code enumeration | Attacker scans codes to discover URLs | 62^7 code space makes brute force slow | No rate limiting on `GET /{code}` |
| Personal data collection | Privacy/compliance exposure | Nothing personal is stored; CMP-001 blocks PII fields in the data model | — |

## Orchestration/process risks
| Risk | Impact | Mitigation | Left open |
|---|---|---|---|
| Stage fails after retries | Pipeline continues on a broken foundation | Fallback if defined, otherwise safe-stop, and the open incident is reported | No alerting: a human reads the console, audit log or exit code |
| Fallback produces weak evidence | Unverified code is shipped as if tested | Fallback output is marked `degraded`; CHG-003 warns the approver and SEC-002 blocks unverified security controls (shown in the fault-injection run) | Only Testing has a fallback |
| Approval fatigue | Gates become rubber stamps | Only two approval points; entry gates run *before* the prompt, so reviewers never approve something policy already rejects; accepted warnings are named in the prompt and logged | — |
| Unaccountable approvals | No record of who approved | Reviewer identity recorded on every approval and in the release notes | Identity is self-declared (`--reviewer` / OS user), not authenticated |
| Code built on a stale design | A re-plan changes the design but old code ships | CHG-001 blocks release unless implementation was built against the latest design revision | — |
| Brownfield change drifts in scope | Unreviewed edits to unaffected modules | CHG-004 blocks changes outside the analysed impact scope; the patch is written to `docs/brownfield/changes.diff` for review | The analyzer is rule-based and only finds what its rules describe |
| Fix doesn't actually fix | Findings silently remain | CHG-005 re-runs the analysis on the patched code, and runtime concurrency tests cover the same defects | — |
| Artifacts change after sign-off | What ships differs from what was reviewed | REL-001 verifies the SHA-256 manifest recorded in the release notes | Nothing re-checks after the pipeline exits |
| Concurrent reset race | A reset is overwritten by a sibling stage in the same wave | Rollbacks and re-plans are deferred until the wave completes; status is a `ConcurrentDictionary`; unit-tested | Two conflicting resets in one wave are applied in arrival order |
| A gate itself crashes | The check is silently skipped | A gate exception becomes a GATE-ERR Block (fails closed); unit-tested | — |
| Guardrails are too simple | Easy to satisfy without real quality | Evidence comes from executed tests with per-test results, not counts alone | Regex scans are heuristics; a real system would add SAST and dependency scanning |

## Policy catalogue
| Id | Category | Rule | Severity | Where |
|---|---|---|---|---|
| SEC-001 | Security | No hardcoded secrets (credential assignments, connection-string passwords, private keys) | Block | implementation exit |
| SEC-002 | Security | Redirect scheme allow-list verified by executed, passing unit + HTTP tests | Block | release entry |
| CMP-001 | Compliance | No PII fields (IP, user-agent, referrer, contact, location) in the data model | Block | implementation exit |
| CHG-001 | Change control | Implementation approved and built against the latest design revision | Block | release entry |
| CHG-002 | Change control | ≥ 2 tests (else roll back to testing); no failing tests | Rollback / Block | release entry |
| CHG-003 | Change control | Test evidence is degraded (not executed) | Warn | release entry |
| CHG-004 | Change control | Brownfield changes stay within the analysed impact scope | Block | implementation exit |
| CHG-005 | Change control | Re-analysis shows all brownfield findings cleared | Block | implementation exit |
| REQ-001/002, DES-001/002, TST-001, DOC-001, REL-001 | Stage quality | Each stage's output is complete (sections, endpoints, tests discovered, README coverage, manifest integrity) | Block (REQ-002 Warn) | per stage |

## Validation approach
1. **Every control-flow path is unit-tested.** `orchestrator.Tests` has 32
   tests covering parallel waves, approvals, retries, fallback, gate
   blocks and rollbacks, re-planning, fail-closed gates, audit lineage,
   policies, the analyzer and the diff.
2. **Real test execution as evidence.** Testing runs `dotnet test` and
   parses the TRX file, so release decisions are based on per-test outcomes
   rather than on a hardcoded count.
3. **End-to-end runs of every scenario plus a failure run.** Observed audit
   excerpts and metrics are in `docs/scenarios.md`, and every claim there can
   be checked against `audit.log`.
4. **Audit trail as source of truth.** Every gate result, approval
   (with reviewer), retry, fallback, rollback, re-plan and context decision
   is one JSON line in `audit.log`.
5. **Runtime tests for the product's reliability claims.** The concurrency
   and scheme-rejection tests prove the fixes behave correctly, not just that
   the code looks right.

## What's explicitly out of scope (and why)
- Load/performance testing: no scale requirement was given (see the
  ambiguous scenario), so optimising for an unknown target would be premature.
- Multi-instance/distributed concerns: a 2-3 day prototype doesn't justify
  distributed locking or a shared store.
- An authenticated reviewer identity and permissions model: the console
  approver shows the control flow, and a production system would need this first.
