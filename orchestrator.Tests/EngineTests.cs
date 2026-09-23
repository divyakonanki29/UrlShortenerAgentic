using System.Collections.Concurrent;
using System.Text.Json;
using Orchestrator;
using Xunit;

namespace Orchestrator.Tests;

/// <summary>
/// Scheduling, governance and recovery behaviour of PipelineEngine, using
/// in-memory stages so each control-flow path is exercised in isolation from
/// file I/O and the real SDLC stages.
/// </summary>
public class EngineTests : IDisposable
{
    private readonly string _auditPath = Path.Combine(Path.GetTempPath(), $"engine-test-{Guid.NewGuid():N}.log");
    private readonly ConcurrentDictionary<string, int> _runs = new();

    public void Dispose() => File.Delete(_auditPath);

    private (PipelineEngine Engine, PipelineContext Context) Build(
        Func<ApprovalRequest, ApprovalDecision>? approve = null, params StageDefinition[] stages)
    {
        var ctx = new PipelineContext { Scenario = "test" };
        var engine = new PipelineEngine(ctx, new AuditLogger(_auditPath),
            approve ?? (_ => new ApprovalDecision(true, "tester")),
            retryBackoff: _ => TimeSpan.Zero);
        foreach (var s in stages) engine.Register(s);
        return (engine, ctx);
    }

    private StageDefinition Stage(string name, string[]? deps = null, Func<PipelineContext, Task<StageResult>>? execute = null,
        bool approval = false, Func<PipelineContext, Task<StageResult>>? fallback = null,
        Func<PipelineContext, GateResult>? entry = null, Func<PipelineContext, GateResult>? exit = null) => new()
    {
        Name = name,
        DependsOn = deps ?? Array.Empty<string>(),
        RequiresApproval = approval,
        Fallback = fallback,
        EntryGate = entry,
        ExitGate = exit,
        Execute = async ctx =>
        {
            _runs.AddOrUpdate(name, 1, (_, n) => n + 1);
            return execute is null ? StageResult.Ok($"{name} done") : await execute(ctx);
        }
    };

    private int Runs(string name) => _runs.GetValueOrDefault(name);

    private List<(string Stage, string Event, string Details)> Audit() =>
        File.ReadAllLines(_auditPath).Select(l =>
        {
            var e = JsonDocument.Parse(l).RootElement;
            return (e.GetProperty("stage").GetString()!, e.GetProperty("eventType").GetString()!, e.GetProperty("details").GetString()!);
        }).ToList();

    [Fact]
    public async Task Independent_stages_run_in_the_same_wave_concurrently()
    {
        int running = 0, maxConcurrent = 0;
        async Task<StageResult> Slow(PipelineContext _)
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxConcurrent, now);
            await Task.Delay(100);
            Interlocked.Decrement(ref running);
            return StageResult.Ok("slow");
        }

        var (engine, _) = Build(null,
            Stage("a"), Stage("b", new[] { "a" }, Slow), Stage("c", new[] { "a" }, Slow), Stage("d", new[] { "b", "c" }));
        await engine.RunAsync();

        Assert.Equal(2, maxConcurrent);
        Assert.All(engine.Status.Values, s => Assert.Equal(StageStatus.Succeeded, s));
        // d synchronises on both parallel branches: it starts only after b and c succeeded.
        var events = Audit();
        int dStart = events.FindIndex(e => e.Stage == "d" && e.Event == "start");
        Assert.True(dStart > events.FindLastIndex(e => e.Stage is "b" or "c" && e.Event == "succeeded"));
    }

    [Fact]
    public async Task Rejected_approval_safe_stops_and_skips_downstream_stages()
    {
        var (engine, _) = Build(_ => new ApprovalDecision(false, "reviewer-x"),
            Stage("build"), Stage("ship", new[] { "build" }, approval: true), Stage("announce", new[] { "ship" }));
        await engine.RunAsync();

        Assert.True(engine.SafeStopped);
        Assert.Equal(StageStatus.Rejected, engine.Status["ship"]);
        Assert.Equal(0, Runs("ship"));
        Assert.Equal(0, Runs("announce"));
        Assert.Contains(Audit(), e => e.Event == "approval" && e.Details == "rejected by reviewer-x");
        Assert.Equal("safe-stop", engine.Metrics.Summarize().Outcome);
    }

    [Fact]
    public async Task Approval_records_reviewer_and_accepted_warnings()
    {
        ApprovalRequest? seen = null;
        var (engine, ctx) = Build(r => { seen = r; return new ApprovalDecision(true, "alice"); },
            Stage("ship", approval: true, entry: _ => GateResult.From(PolicyFinding.Warn("CHG-003", "degraded evidence"))));
        await engine.RunAsync();

        Assert.Equal("CHG-003", Assert.Single(seen!.Warnings).PolicyId);
        Assert.Equal("alice", ctx.Get("ship.approvedBy"));
        Assert.Contains(Audit(), e => e.Event == "approval" && e.Details == "approved by alice; accepted warnings: CHG-003");
    }

    [Fact]
    public async Task Transient_failure_is_retried_and_counted_as_a_resolved_incident()
    {
        int attempts = 0;
        var (engine, _) = Build(null,
            Stage("flaky", execute: _ => Task.FromResult(++attempts < 3 ? StageResult.Fail("transient") : StageResult.Ok("ok"))));
        await engine.RunAsync();

        var summary = engine.Metrics.Summarize();
        Assert.Equal(StageStatus.Succeeded, engine.Status["flaky"]);
        Assert.Equal(2, summary.TotalRetries);
        Assert.Equal(1, summary.IncidentsResolved);
        Assert.Equal(0, summary.IncidentsOpen);
        Assert.NotNull(summary.MttrMs);
    }

    [Fact]
    public async Task Exhausted_retries_fall_back_to_degraded_path()
    {
        var (engine, ctx) = Build(null,
            Stage("tests",
                execute: _ => throw new InvalidOperationException("runner down"),
                fallback: _ => Task.FromResult(StageResult.Ok("inventoried"))),
            Stage("release", new[] { "tests" }));
        await engine.RunAsync();

        Assert.Equal(StageStatus.Succeeded, engine.Status["tests"]);
        Assert.Equal(3, Runs("tests")); // 1 attempt + 2 retries before falling back
        Assert.Equal("true", ctx.Get("tests.degraded"));
        Assert.Equal(1, engine.Metrics.Summarize().TotalFallbacks);
        Assert.Contains(Audit(), e => e.Stage == "tests" && e.Event == "fallback");
        Assert.Contains(Audit(), e => e.Stage == "tests" && e.Event == "succeeded" && e.Details.Contains("DEGRADED"));
        Assert.Equal(StageStatus.Succeeded, engine.Status["release"]);
    }

    [Fact]
    public async Task Exhausted_retries_without_fallback_safe_stop_with_open_incident()
    {
        var (engine, _) = Build(null,
            Stage("broken", execute: _ => Task.FromResult(StageResult.Fail("always"))),
            Stage("after", new[] { "broken" }));
        await engine.RunAsync();

        Assert.True(engine.SafeStopped);
        Assert.Equal(StageStatus.Failed, engine.Status["broken"]);
        Assert.Equal(0, Runs("after"));
        Assert.Equal(1, engine.Metrics.Summarize().IncidentsOpen);
    }

    [Fact]
    public async Task Entry_gate_block_stops_before_approval_and_execution()
    {
        bool asked = false;
        var (engine, _) = Build(_ => { asked = true; return new ApprovalDecision(true, "t"); },
            Stage("ship", approval: true, entry: _ => GateResult.From(PolicyFinding.Block("SEC-002", "unverified"))));
        await engine.RunAsync();

        Assert.False(asked);
        Assert.Equal(0, Runs("ship"));
        Assert.Equal(StageStatus.Blocked, engine.Status["ship"]);
        Assert.Contains(Audit(), e => e.Event == "gate-blocked" && e.Details.Contains("SEC-002"));
    }

    [Fact]
    public async Task Exit_gate_block_rejects_the_stage_output()
    {
        var (engine, _) = Build(null,
            Stage("impl", exit: _ => GateResult.From(PolicyFinding.Block("SEC-001", "secret found"))),
            Stage("after", new[] { "impl" }));
        await engine.RunAsync();

        Assert.Equal(1, Runs("impl"));
        Assert.Equal(StageStatus.Blocked, engine.Status["impl"]);
        Assert.Equal(0, Runs("after"));
    }

    [Fact]
    public async Task Gate_that_throws_fails_closed()
    {
        var (engine, _) = Build(null, Stage("ship", entry: _ => throw new IOException("disk gone")));
        await engine.RunAsync();

        Assert.Equal(StageStatus.Blocked, engine.Status["ship"]);
        Assert.Contains(Audit(), e => e.Event == "policy" && e.Details.Contains("GATE-ERR"));
    }

    [Fact]
    public async Task Gate_rollback_resets_target_and_dependents_then_recovers()
    {
        int gateChecks = 0;
        var (engine, _) = Build(null,
            Stage("impl"),
            Stage("tests", new[] { "impl" }),
            Stage("release", new[] { "tests" }, entry: _ => ++gateChecks == 1
                ? new GateResult { Findings = new[] { PolicyFinding.Warn("CHG-002", "too few tests") }, RollbackTarget = "tests" }
                : GateResult.From(PolicyFinding.Pass("CHG-002", "ok"))));
        await engine.RunAsync();

        Assert.Equal("complete", engine.Metrics.Summarize().Outcome);
        Assert.Equal(1, Runs("impl"));
        Assert.Equal(2, Runs("tests"));
        Assert.Equal(1, Runs("release"));
        var summary = engine.Metrics.Summarize();
        Assert.Equal(1, summary.IncidentsResolved); // opened by the rollback, resolved when release succeeds
        Assert.Contains(Audit(), e => e.Stage == "release" && e.Event == "guardrail-rollback");
    }

    [Fact]
    public async Task Replan_reruns_succeeded_stages_and_is_not_lost_to_a_slow_sibling()
    {
        bool replanned = false;
        var (engine, _) = Build(null,
            Stage("design"),
            Stage("impl", new[] { "design" }),
            Stage("tests", new[] { "impl" }, _ =>
            {
                if (replanned) return Task.FromResult(StageResult.Ok("tests pass"));
                replanned = true;
                return Task.FromResult(new StageResult { Success = true, Output = "clarified", ReplanFrom = "design" });
            }),
            // Still running when "tests" requests the re-plan. Without deferred resets
            // it would mark itself Succeeded after being reset and never re-run.
            Stage("docs", new[] { "impl" }, async _ => { await Task.Delay(150); return StageResult.Ok("docs"); }));
        await engine.RunAsync();

        Assert.Equal("complete", engine.Metrics.Summarize().Outcome);
        Assert.Equal(2, Runs("design"));
        Assert.Equal(2, Runs("impl"));
        Assert.Equal(2, Runs("tests"));
        Assert.Equal(2, Runs("docs"));
        Assert.Equal(4, Audit().Count(e => e.Event == "replan"));
    }

    [Fact]
    public async Task Context_decisions_are_written_to_the_audit_log_with_their_stage()
    {
        var (engine, _) = Build(null,
            Stage("design", execute: ctx => { ctx.Record("design.revision", "1"); return Task.FromResult(StageResult.Ok("ok")); }));
        await engine.RunAsync();

        Assert.Contains(Audit(), e => e.Stage == "design" && e.Event == "decision" && e.Details == "design.revision=1");
    }

    [Fact]
    public void Registering_a_stage_before_its_dependency_is_rejected()
    {
        var (engine, _) = Build();
        Assert.Throws<InvalidOperationException>(() => engine.Register(Stage("b", new[] { "a" })));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
