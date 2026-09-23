using System.Text.Json;

namespace Orchestrator;

public class StageMetric
{
    public string Stage { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int Runs { get; set; }
    public int RetryCount { get; set; }
    public int RollbackCount { get; set; }
    public int FallbackCount { get; set; }
    public StageStatus FinalStatus { get; set; } = StageStatus.Pending;

    /// <summary>Total time across every run of this stage (reruns after rollback/replan included).</summary>
    public double DurationMs { get; set; }
}

/// <summary>
/// An incident opens when a stage hits trouble (first failed attempt, or a gate
/// that forces a rollback) and resolves when that same stage next succeeds.
/// MTTR is the mean open-to-resolved time across resolved incidents.
/// </summary>
public record Incident(string Stage, string Reason, DateTimeOffset OpenedAt, DateTimeOffset? ResolvedAt)
{
    public double? RecoveryMs => ResolvedAt is { } r ? (r - OpenedAt).TotalMilliseconds : null;
}

public record PipelineSummary(
    string Outcome,
    int Stages,
    int StagesSucceeded,
    double SuccessRatePct,
    int TotalRetries,
    int TotalRollbacks,
    int TotalFallbacks,
    int IncidentsResolved,
    int IncidentsOpen,
    double? MttrMs,
    int ApprovalsRequested,
    double ApprovalWaitMs,
    double StageTimeMs,
    double WallClockMs,
    double AutomatedLatencyMs);

/// <summary>
/// One metric record per stage for the whole pipeline run. Re-running a stage
/// (after rollback or re-plan) reuses its record so retry/rollback counts and
/// duration accumulate instead of being reset by the latest attempt.
/// </summary>
public class MetricsTracker
{
    private readonly Dictionary<string, StageMetric> _metrics = new();
    private readonly List<Incident> _incidents = new();
    private readonly object _lock = new(); // stages in the same wave report concurrently
    private DateTimeOffset _pipelineStart;
    private DateTimeOffset? _pipelineEnd;
    private string _outcome = "not-started";
    private int _approvalsRequested;
    private double _approvalWaitMs;

    public void RegisterStage(string stage)
    {
        lock (_lock) GetOrCreate(stage);
    }

    private StageMetric GetOrCreate(string stage)
    {
        if (!_metrics.TryGetValue(stage, out var m))
        {
            m = new StageMetric { Stage = stage };
            _metrics[stage] = m;
        }
        return m;
    }

    public IReadOnlyDictionary<string, StageMetric> Stages
    {
        get { lock (_lock) return new Dictionary<string, StageMetric>(_metrics); }
    }

    public IReadOnlyList<Incident> Incidents
    {
        get { lock (_lock) return _incidents.ToList(); }
    }

    public void PipelineStarted()
    {
        lock (_lock) { _pipelineStart = DateTimeOffset.UtcNow; _outcome = "running"; }
    }

    public void PipelineEnded(string outcome)
    {
        lock (_lock) { _pipelineEnd = DateTimeOffset.UtcNow; _outcome = outcome; }
    }

    public StageMetric Begin(string stage)
    {
        lock (_lock)
        {
            var m = GetOrCreate(stage);
            m.StartedAt = DateTimeOffset.UtcNow;
            m.EndedAt = null;
            m.Runs++;
            return m;
        }
    }

    public void End(string stage, StageStatus status)
    {
        lock (_lock)
        {
            var m = GetOrCreate(stage);
            m.EndedAt = DateTimeOffset.UtcNow;
            m.DurationMs += (m.EndedAt.Value - m.StartedAt).TotalMilliseconds;
            m.FinalStatus = status;
        }
    }

    /// <summary>For stages that end without executing (blocked/rolled back at an entry gate, rejected).</summary>
    public void SetStatus(string stage, StageStatus status)
    {
        lock (_lock) GetOrCreate(stage).FinalStatus = status;
    }

    public void IncrementRetry(string stage)
    {
        lock (_lock) GetOrCreate(stage).RetryCount++;
    }

    public void IncrementFallback(string stage)
    {
        lock (_lock) GetOrCreate(stage).FallbackCount++;
    }

    // Stages that never ran (e.g. reset by a re-plan before their first run)
    // have nothing to roll back, so they are not counted.
    public void IncrementRollback(string stage)
    {
        lock (_lock)
        {
            if (_metrics.TryGetValue(stage, out var m) && m.Runs > 0) m.RollbackCount++;
        }
    }

    public void RecordApprovalWait(TimeSpan wait)
    {
        lock (_lock)
        {
            _approvalsRequested++;
            _approvalWaitMs += wait.TotalMilliseconds;
        }
    }

    /// <summary>Opens an incident for the stage unless one is already open.</summary>
    public void OpenIncident(string stage, string reason)
    {
        lock (_lock)
        {
            if (_incidents.Any(i => i.Stage == stage && i.ResolvedAt is null)) return;
            _incidents.Add(new Incident(stage, reason, DateTimeOffset.UtcNow, null));
        }
    }

    public void ResolveIncident(string stage)
    {
        lock (_lock)
        {
            var idx = _incidents.FindIndex(i => i.Stage == stage && i.ResolvedAt is null);
            if (idx >= 0) _incidents[idx] = _incidents[idx] with { ResolvedAt = DateTimeOffset.UtcNow };
        }
    }

    public PipelineSummary Summarize()
    {
        lock (_lock)
        {
            int total = _metrics.Count;
            int succeeded = _metrics.Values.Count(m => m.FinalStatus == StageStatus.Succeeded);
            var resolved = _incidents.Where(i => i.ResolvedAt is not null).ToList();
            double wall = ((_pipelineEnd ?? DateTimeOffset.UtcNow) - _pipelineStart).TotalMilliseconds;

            return new PipelineSummary(
                Outcome: _outcome,
                Stages: total,
                StagesSucceeded: succeeded,
                SuccessRatePct: total == 0 ? 0 : Math.Round((double)succeeded / total * 100, 1),
                TotalRetries: _metrics.Values.Sum(m => m.RetryCount),
                TotalRollbacks: _metrics.Values.Sum(m => m.RollbackCount),
                TotalFallbacks: _metrics.Values.Sum(m => m.FallbackCount),
                IncidentsResolved: resolved.Count,
                IncidentsOpen: _incidents.Count - resolved.Count,
                MttrMs: resolved.Count == 0 ? null : Math.Round(resolved.Average(i => i.RecoveryMs!.Value), 1),
                ApprovalsRequested: _approvalsRequested,
                ApprovalWaitMs: Math.Round(_approvalWaitMs, 1),
                StageTimeMs: Math.Round(_metrics.Values.Sum(m => m.DurationMs), 1),
                WallClockMs: Math.Round(wall, 1),
                // End-to-end latency with human think-time removed: what the automation itself costs.
                AutomatedLatencyMs: Math.Round(Math.Max(0, wall - _approvalWaitMs), 1));
        }
    }

    public void PrintSummary()
    {
        var s = Summarize();
        Console.WriteLine();
        Console.WriteLine("=== Reliability Metrics ===");
        foreach (var m in Stages.Values)
        {
            Console.WriteLine($"  {m.Stage,-14} status={m.FinalStatus,-12} duration={m.DurationMs,8:F0}ms runs={m.Runs} retries={m.RetryCount} rollbacks={m.RollbackCount} fallbacks={m.FallbackCount}");
        }

        Console.WriteLine($"Outcome: {s.Outcome}  |  Success rate: {s.SuccessRatePct:F1}% ({s.StagesSucceeded}/{s.Stages})  |  Retries: {s.TotalRetries}  |  Rollbacks: {s.TotalRollbacks}  |  Fallbacks: {s.TotalFallbacks}");
        Console.WriteLine($"Incidents: {s.IncidentsResolved} resolved, {s.IncidentsOpen} open  |  MTTR: {(s.MttrMs is { } mttr ? $"{mttr:F0}ms" : "n/a")}");
        Console.WriteLine($"E2E wall clock: {s.WallClockMs:F0}ms  (automated: {s.AutomatedLatencyMs:F0}ms, waiting on {s.ApprovalsRequested} approval(s): {s.ApprovalWaitMs:F0}ms)");
    }

    public void WriteJson(string path)
    {
        var payload = new
        {
            summary = Summarize(),
            stages = Stages.Values.Select(m => new
            {
                m.Stage,
                status = m.FinalStatus.ToString(),
                durationMs = Math.Round(m.DurationMs, 1),
                m.Runs,
                m.RetryCount,
                m.RollbackCount,
                m.FallbackCount
            }),
            incidents = Incidents.Select(i => new
            {
                i.Stage,
                i.Reason,
                openedAt = i.OpenedAt,
                resolvedAt = i.ResolvedAt,
                recoveryMs = i.RecoveryMs is { } r ? Math.Round(r, 1) : (double?)null
            })
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }
}
