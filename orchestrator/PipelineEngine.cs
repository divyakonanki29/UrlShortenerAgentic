using System.Collections.Concurrent;
using System.Diagnostics;

namespace Orchestrator;

/// <summary>
/// Coordinates stage execution across the whole SDLC lifecycle.
///
/// - Non-linear: stages whose dependencies are all satisfied run together in a
///   "wave" (Task.WhenAll), so independent stages (e.g. Testing + Docs) run in
///   parallel while dependent ones stay sequential.
/// - Gated: each stage can declare an entry gate (preconditions, checked before
///   its approval prompt) and an exit gate (checks on its output). Gate findings
///   are Pass/Warn/Block; any Block safe-stops the run, and a gate can instead
///   request a rollback to an earlier stage.
/// - Stateful: stage status + artifacts persist in PipelineContext/_status across
///   the whole run, and are what re-planning resets/rewinds. Every context
///   write is mirrored to the audit log as a "decision" event.
/// - Governed: RequiresApproval stages block for a human decision before running;
///   the reviewer's identity and any warnings they accepted are recorded.
/// - Resilient: bounded retries per stage, an optional degraded fallback,
///   rollback of dependents on guardrail failure, and a safe-stop if a stage
///   cannot recover.
/// </summary>
public class PipelineEngine
{
    private readonly Dictionary<string, StageDefinition> _stages = new();
    private readonly ConcurrentDictionary<string, StageStatus> _status = new();
    private readonly ConcurrentQueue<Action> _deferredResets = new();
    private readonly AuditLogger _audit;
    private readonly MetricsTracker _metrics = new();
    private readonly PipelineContext _context;
    private readonly Func<ApprovalRequest, ApprovalDecision> _approve;
    private readonly Func<int, TimeSpan> _retryBackoff;
    private volatile bool _safeStop;

    /// <param name="approve">Human approval provider (console in Program.cs; a stub in tests).</param>
    /// <param name="retryBackoff">Delay before retry attempt n (default 200ms * n).</param>
    public PipelineEngine(PipelineContext context, AuditLogger audit,
        Func<ApprovalRequest, ApprovalDecision> approve, Func<int, TimeSpan>? retryBackoff = null)
    {
        _context = context;
        _audit = audit;
        _approve = approve;
        _retryBackoff = retryBackoff ?? (attempt => TimeSpan.FromMilliseconds(200 * attempt));
        _context.OnRecord = (stage, key, value) => _audit.Log(stage, "decision", $"{key}={value}");
    }

    public void Register(StageDefinition stage)
    {
        foreach (var dep in stage.DependsOn)
        {
            if (!_stages.ContainsKey(dep))
                throw new InvalidOperationException($"Stage '{stage.Name}' depends on unregistered stage '{dep}' (register dependencies first)");
        }
        _stages[stage.Name] = stage;
        _status[stage.Name] = StageStatus.Pending;
        _metrics.RegisterStage(stage.Name);
    }

    public MetricsTracker Metrics => _metrics;
    public IReadOnlyDictionary<string, StageStatus> Status => _status;
    public bool SafeStopped => _safeStop;

    public async Task RunAsync()
    {
        _metrics.PipelineStarted();
        _audit.Log("pipeline", "start", $"scenario={_context.Scenario}");

        while (!_safeStop && _status.Values.Any(s => s == StageStatus.Pending))
        {
            var ready = _stages.Values
                .Where(s => _status[s.Name] == StageStatus.Pending)
                .Where(s => s.DependsOn.All(dep => _status.GetValueOrDefault(dep) == StageStatus.Succeeded))
                .ToList();

            if (ready.Count == 0) break; // pending stages whose dependencies can never succeed

            // Run this wave in parallel.
            await Task.WhenAll(ready.Select(RunStageAsync));

            // Apply rollbacks/re-plans only once the whole wave has finished, so
            // a reset can't race a sibling stage that is still running (e.g.
            // Testing re-planning while Docs, in the same wave, would otherwise
            // mark itself Succeeded after being reset).
            while (_deferredResets.TryDequeue(out var reset))
                reset();
        }

        string outcome = _safeStop ? "safe-stop"
            : _status.Values.Any(s => s != StageStatus.Succeeded) ? "stalled"
            : "complete";
        string detail = outcome switch
        {
            "safe-stop" => "halted: a stage was rejected, blocked by policy, or failed without recovery",
            "stalled" => "halted: pending stages have dependencies that can never succeed",
            _ => "all stages resolved"
        };
        _metrics.PipelineEnded(outcome);
        _audit.Log("pipeline", outcome, detail);
    }

    private async Task RunStageAsync(StageDefinition stage)
    {
        // Flows into stage code (and its awaits) so context writes are attributed to this stage.
        _context.CurrentStage = stage.Name;

        IReadOnlyList<PolicyFinding> acceptedWarnings = Array.Empty<PolicyFinding>();
        if (stage.EntryGate is not null)
        {
            var gate = EvaluateGate(stage, "entry", stage.EntryGate);
            if (gate is null) return;
            acceptedWarnings = gate.Warnings;
        }

        if (stage.RequiresApproval)
        {
            _status[stage.Name] = StageStatus.AwaitingApproval;
            var waited = Stopwatch.StartNew();
            var decision = _approve(new ApprovalRequest(stage.Name, acceptedWarnings));
            _metrics.RecordApprovalWait(waited.Elapsed);

            if (!decision.Approved)
            {
                _status[stage.Name] = StageStatus.Rejected;
                _metrics.SetStatus(stage.Name, StageStatus.Rejected);
                _audit.Log(stage.Name, "approval", $"rejected by {decision.Reviewer}");
                _safeStop = true;
                return;
            }

            var accepted = acceptedWarnings.Count == 0
                ? ""
                : $"; accepted warnings: {string.Join(", ", acceptedWarnings.Select(w => w.PolicyId))}";
            _audit.Log(stage.Name, "approval", $"approved by {decision.Reviewer}{accepted}");
            _context.Record($"{stage.Name}.approvedBy", decision.Reviewer);
        }

        _metrics.Begin(stage.Name);
        _status[stage.Name] = StageStatus.Running;
        _audit.Log(stage.Name, "start", "");

        var result = await ExecuteWithRetriesAsync(stage);
        bool degraded = false;

        if (!result.Success && stage.Fallback is not null)
        {
            _metrics.IncrementFallback(stage.Name);
            _audit.Log(stage.Name, "fallback", $"primary path exhausted retries ({result.Error}); running degraded fallback");
            try
            {
                result = await stage.Fallback(_context);
            }
            catch (Exception ex)
            {
                result = StageResult.Fail($"fallback threw: {ex.Message}");
            }
            degraded = result.Success;
        }

        if (!result.Success)
        {
            _status[stage.Name] = StageStatus.Failed;
            _metrics.End(stage.Name, StageStatus.Failed);
            _audit.Log(stage.Name, "failed", result.Error ?? "unknown error");
            _safeStop = true; // no recovery path left for this stage -> halt safely
            return;
        }

        _context.Record($"{stage.Name}.degraded", degraded ? "true" : "false");

        // A stage that requests a re-plan is about to be reset and re-run, so its
        // current output is discarded rather than validated.
        bool replanning = !string.IsNullOrEmpty(result.ReplanFrom);
        if (stage.ExitGate is not null && !replanning && EvaluateGate(stage, "exit", stage.ExitGate) is null)
            return;

        _status[stage.Name] = StageStatus.Succeeded;
        _metrics.End(stage.Name, StageStatus.Succeeded);
        _metrics.ResolveIncident(stage.Name);
        _audit.Log(stage.Name, "succeeded", degraded ? $"{result.Output} [DEGRADED: via fallback]" : result.Output);

        if (replanning)
        {
            var from = result.ReplanFrom!;
            _deferredResets.Enqueue(() => RePlan(from, stage.Name));
        }
    }

    private async Task<StageResult> ExecuteWithRetriesAsync(StageDefinition stage)
    {
        StageResult result = StageResult.Fail("not executed");
        for (int attempt = 0; attempt <= stage.MaxRetries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(_retryBackoff(attempt));

            try
            {
                result = await stage.Execute(_context);
                if (result.Success) return result;
                _audit.Log(stage.Name, "retry", $"attempt={attempt + 1} error={result.Error}");
            }
            catch (Exception ex)
            {
                result = StageResult.Fail(ex.Message);
                _audit.Log(stage.Name, "exception", $"attempt={attempt + 1} error={ex.Message}");
            }

            _metrics.OpenIncident(stage.Name, $"attempt failed: {result.Error}");
            if (attempt < stage.MaxRetries) _metrics.IncrementRetry(stage.Name);
        }
        return result;
    }

    /// <summary>
    /// Evaluates a gate, logs every finding, and applies its verdict. Returns the
    /// gate result when the stage may proceed, or null when the gate stopped it
    /// (blocked -> safe-stop, or rollback requested).
    /// </summary>
    private GateResult? EvaluateGate(StageDefinition stage, string phase, Func<PipelineContext, GateResult> check)
    {
        GateResult gate;
        try
        {
            gate = check(_context);
        }
        catch (Exception ex)
        {
            // A gate that can't be evaluated fails closed.
            gate = GateResult.From(PolicyFinding.Block("GATE-ERR", $"{phase} gate threw: {ex.Message}"));
        }

        foreach (var f in gate.Findings)
            _audit.Log(stage.Name, "policy", $"{phase}-gate {f}");
        _context.Record($"gate.{stage.Name}.{phase}",
            gate.Findings.Count == 0 ? "no checks" : string.Join("; ", gate.Findings.Select(f => $"{f.PolicyId}={f.Severity}")));

        bool executed = phase == "exit";
        if (gate.IsBlocked)
        {
            var reasons = string.Join(" | ", gate.Findings.Where(f => f.Severity == PolicySeverity.Block).Select(f => f.ToString()));
            _status[stage.Name] = StageStatus.Blocked;
            if (executed) _metrics.End(stage.Name, StageStatus.Blocked);
            else _metrics.SetStatus(stage.Name, StageStatus.Blocked);
            _audit.Log(stage.Name, "gate-blocked", $"{phase} gate: {reasons}");
            _safeStop = true;
            return null;
        }

        if (gate.RollbackTarget is { } target)
        {
            var reasons = string.Join(" | ", gate.Findings.Where(f => f.Severity != PolicySeverity.Pass).Select(f => f.ToString()));
            _status[stage.Name] = StageStatus.RolledBack;
            if (executed) _metrics.End(stage.Name, StageStatus.RolledBack);
            else _metrics.SetStatus(stage.Name, StageStatus.RolledBack);
            _metrics.OpenIncident(stage.Name, $"{phase} gate rollback to {target}: {reasons}");
            _audit.Log(stage.Name, "guardrail-rollback", $"target={target} gate={phase} reason={reasons}");
            _deferredResets.Enqueue(() => RollbackTo(target));
            return null;
        }

        return gate;
    }

    /// <summary>
    /// Dynamic re-planning: when a stage learns something that makes earlier
    /// work stale (e.g. a clarification that changes the design), reset the
    /// stale stage and every stage that transitively depends on it back to
    /// Pending so they re-execute with the new context — including stages
    /// that already succeeded — instead of continuing on stale assumptions.
    /// </summary>
    private void RePlan(string staleStage, string raisedBy)
    {
        var toReset = new[] { staleStage }.Concat(TransitiveDependents(staleStage));
        foreach (var name in toReset)
        {
            _status[name] = StageStatus.Pending;
            _metrics.IncrementRollback(name);
            _audit.Log(name, "replan", $"reset: {staleStage} invalidated by clarification raised in {raisedBy}");
        }
    }

    /// <summary>
    /// Used by gates that decide the pipeline should go back to an earlier stage
    /// (e.g. Release finding too few tests rolls back to Testing) rather than
    /// failing outright. Resets the target and everything downstream of it.
    /// </summary>
    private void RollbackTo(string stageName)
    {
        var toReset = new[] { stageName }.Concat(TransitiveDependents(stageName));
        foreach (var name in toReset)
        {
            _status[name] = StageStatus.Pending;
            _metrics.IncrementRollback(name);
            _audit.Log(name, "rollback", $"rolled back to {stageName}");
        }
    }

    private IEnumerable<string> TransitiveDependents(string stageName)
    {
        var result = new HashSet<string>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var s in _stages.Values)
            {
                if (result.Contains(s.Name)) continue;
                if (s.DependsOn.Contains(stageName) || s.DependsOn.Any(result.Contains))
                {
                    result.Add(s.Name);
                    changed = true;
                }
            }
        }
        return result;
    }
}
