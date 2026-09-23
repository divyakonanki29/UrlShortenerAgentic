using System.Collections.Concurrent;

namespace Orchestrator;

public enum StageStatus
{
    Pending,
    AwaitingApproval,
    Rejected,
    Running,
    Succeeded,
    Failed,
    RolledBack,
    Blocked
}

/// <summary>
/// Result returned by a stage's work function.
/// </summary>
public class StageResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = string.Empty;
    public string? Error { get; init; }

    /// <summary>
    /// Set by a stage that learns something invalidating earlier work (e.g. a
    /// requirement clarification that changes the design). Names the earliest
    /// stage whose output is now stale; the engine re-plans by resetting that
    /// stage and everything that transitively depends on it back to Pending,
    /// so they re-run with the new context instead of continuing on stale
    /// assumptions.
    /// </summary>
    public string? ReplanFrom { get; init; }

    public static StageResult Ok(string output) => new() { Success = true, Output = output };
    public static StageResult Fail(string error) => new() { Success = false, Error = error };
}

public enum PolicySeverity
{
    Pass,
    Warn,
    Block
}

/// <summary>
/// Outcome of one policy/guardrail check. Ids are stable (SEC-*, CMP-*, CHG-*,
/// ...) so audit entries and release notes can be traced back to the rule.
/// </summary>
public record PolicyFinding(string PolicyId, PolicySeverity Severity, string Message)
{
    public static PolicyFinding Pass(string id, string message) => new(id, PolicySeverity.Pass, message);
    public static PolicyFinding Warn(string id, string message) => new(id, PolicySeverity.Warn, message);
    public static PolicyFinding Block(string id, string message) => new(id, PolicySeverity.Block, message);

    public override string ToString() => $"{PolicyId} {Severity.ToString().ToUpperInvariant()}: {Message}";
}

/// <summary>
/// Result of an entry gate (checked before a stage - and before its approval
/// prompt) or an exit gate (checked on the stage's output before it counts as
/// Succeeded). Any Block finding stops the pipeline safely; otherwise a
/// RollbackTarget sends the pipeline back to an earlier stage to fix the gap.
/// </summary>
public class GateResult
{
    public IReadOnlyList<PolicyFinding> Findings { get; init; } = Array.Empty<PolicyFinding>();
    public string? RollbackTarget { get; init; }

    public bool IsBlocked => Findings.Any(f => f.Severity == PolicySeverity.Block);
    public IReadOnlyList<PolicyFinding> Warnings => Findings.Where(f => f.Severity == PolicySeverity.Warn).ToList();

    public static GateResult From(IEnumerable<PolicyFinding> findings) => new() { Findings = findings.ToList() };
    public static GateResult From(params PolicyFinding[] findings) => new() { Findings = findings };
}

/// <summary>What a human reviewer is asked to approve, including any policy warnings they are accepting.</summary>
public record ApprovalRequest(string Stage, IReadOnlyList<PolicyFinding> Warnings);

public record ApprovalDecision(bool Approved, string Reviewer);

/// <summary>
/// Shared, mutable context passed to every stage. This is how "decision lineage"
/// and cross-stage context are preserved: later stages can read what earlier
/// stages decided/produced instead of re-deriving it. Every Record call is also
/// reported through OnRecord (the engine writes it to the audit log), so the
/// lineage survives the process rather than living only in memory.
/// </summary>
public class PipelineContext
{
    private static readonly AsyncLocal<string?> _currentStage = new();

    public string Scenario { get; init; } = "greenfield";
    public string ProjectRoot { get; init; } = string.Empty;

    /// <summary>Fault-injection switches (e.g. "test-runner") used to exercise retry/fallback paths on demand.</summary>
    public IReadOnlySet<string> InjectedFaults { get; init; } = new HashSet<string>();

    // Stages in the same wave (testing + docs) record concurrently.
    public ConcurrentDictionary<string, string> Artifacts { get; } = new();

    /// <summary>Invoked as (stage, key, value) on every Record.</summary>
    public Action<string, string, string>? OnRecord { get; set; }

    /// <summary>The stage whose code is currently running on this async flow.</summary>
    public string? CurrentStage
    {
        get => _currentStage.Value;
        set => _currentStage.Value = value;
    }

    public void Record(string key, string value)
    {
        Artifacts[key] = value;
        OnRecord?.Invoke(CurrentStage ?? "pipeline", key, value);
    }

    public string Get(string key) => Artifacts.TryGetValue(key, out var v) ? v : string.Empty;
    public bool Has(string key) => Artifacts.ContainsKey(key);
    public int GetInt(string key) => int.TryParse(Get(key), out var n) ? n : 0;
}

/// <summary>
/// Declarative definition of one SDLC stage in the dependency graph.
/// </summary>
public class StageDefinition
{
    public required string Name { get; init; }
    public string[] DependsOn { get; init; } = Array.Empty<string>();
    public bool RequiresApproval { get; init; }
    public int MaxRetries { get; init; } = 2;
    public required Func<PipelineContext, Task<StageResult>> Execute { get; init; }

    /// <summary>
    /// Degraded alternative run once Execute has exhausted its retries. A stage
    /// that succeeds this way is marked "{name}.degraded" = true so downstream
    /// policies can decide whether degraded evidence is good enough.
    /// </summary>
    public Func<PipelineContext, Task<StageResult>>? Fallback { get; init; }

    /// <summary>Preconditions checked before the stage runs (and before its approval prompt).</summary>
    public Func<PipelineContext, GateResult>? EntryGate { get; init; }

    /// <summary>Checks on the stage's output before it is allowed to count as Succeeded.</summary>
    public Func<PipelineContext, GateResult>? ExitGate { get; init; }
}
