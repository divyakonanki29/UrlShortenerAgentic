using System.Security.Cryptography;
using System.Text;

namespace Orchestrator;

/// <summary>
/// Concrete stage logic for the URL shortener pipeline. Each stage writes real
/// artifact files so the audit trail and the deliverable are the same thing,
/// and declares the gates that decide whether its inputs and outputs are good
/// enough to proceed.
/// </summary>
public static class Stages
{
    private static readonly string[] StageOrder = { "requirements", "design", "implementation", "testing", "docs", "release" };
    private static readonly string[] Routes = { "/shorten", "/{code}", "/analytics/{code}" };

    private static string DocsDir(PipelineContext ctx) => Path.Combine(ctx.ProjectRoot, "docs");
    // Brownfield evidence lives in its own folder so other scenarios' runs don't overwrite it.
    private static string BrownfieldDocsDir(PipelineContext ctx) => Path.Combine(DocsDir(ctx), "brownfield");
    private static string GeneratedDir(PipelineContext ctx) => Path.Combine(ctx.ProjectRoot, "generated");
    private static string ApiDir(PipelineContext ctx) => Path.Combine(GeneratedDir(ctx), "UrlShortener.Api");
    private static string TestDir(PipelineContext ctx) => Path.Combine(GeneratedDir(ctx), "UrlShortener.Tests");
    private static string BaselineDir(PipelineContext ctx) => Path.Combine(ctx.ProjectRoot, "baseline", "UrlShortener.Api");
    private static bool IsBrownfield(PipelineContext ctx) => ctx.Scenario == "brownfield";

    public static StageDefinition Requirements() => new()
    {
        Name = "requirements",
        DependsOn = Array.Empty<string>(),
        Execute = async ctx =>
        {
            var docsDir = DocsDir(ctx);
            Directory.CreateDirectory(docsDir);

            string decisions;
            string betterCriterion = "";
            if (IsBrownfield(ctx))
            {
                // Codebase reasoning: analyse the existing service before deciding scope.
                var baseline = BaselineDir(ctx);
                if (!Directory.Exists(baseline))
                    return StageResult.Fail($"brownfield baseline not found at {baseline}");

                var report = CodebaseAnalyzer.Analyze(baseline);
                Directory.CreateDirectory(BrownfieldDocsDir(ctx));
                await File.WriteAllTextAsync(Path.Combine(BrownfieldDocsDir(ctx), "impact-analysis.md"),
                    CodebaseAnalyzer.ToMarkdown(report, "baseline/UrlShortener.Api"));
                ctx.Record("brownfield.impactedFiles", string.Join(",", report.ImpactedFiles));
                ctx.Record("brownfield.unimpactedFiles", string.Join(",", report.UnimpactedFiles));
                ctx.Record("brownfield.findingRules", string.Join(",", report.Findings.Select(f => f.RuleId).Distinct()));

                decisions =
                    "- Change type: BROWNFIELD enhancement to the existing service in `baseline/UrlShortener.Api`.\n" +
                    $"- Codebase reasoning: {report.Files.Count} source files and {report.Endpoints.Count} endpoints analysed; " +
                    $"{report.Findings.Count} findings (rules {ctx.Get("brownfield.findingRules")}). Details with file:line evidence in `docs/brownfield/impact-analysis.md`.\n" +
                    $"- Impacted: {ctx.Get("brownfield.impactedFiles")}. Not impacted: {ctx.Get("brownfield.unimpactedFiles")} (public API contract must not change).\n" +
                    "- Ambiguity resolved: 'improve reliability' interpreted as fixing the analysed defects (concurrency safety, code-reservation race,\n" +
                    "  redirect-scheme validation). Distributed rate limiting is out of scope for this pass.";
            }
            else
            {
                decisions = ctx.Scenario switch
                {
                    "ambiguous" => "- Requirement received as-is: 'make the URL shortener better'.\n" +
                                   "- Ambiguities identified: (1) 'better' undefined - could mean performance, reliability, or features;\n" +
                                   "  (2) no target scale given; (3) no auth model specified for analytics endpoint.\n" +
                                   "- Normalized interpretation: prioritize correctness + basic reliability (validation, collision handling)\n" +
                                   "  over new features, given no scale/auth requirements were provided. Flagged as assumption below.",
                    _ => "- Change type: GREENFIELD - new URL shortener service.\n" +
                         "- Ambiguity resolved: custom aliases considered OUT OF SCOPE (not mentioned in the ask).\n" +
                         "- Ambiguity resolved: analytics defined as click count + last-accessed timestamp (no PII/referrer tracking)."
                };
                if (ctx.Scenario == "ambiguous")
                    betterCriterion = "\n- What 'better' must achieve: OPEN - no measurable criterion given; to be clarified with the stakeholder.";
            }

            var content = $$"""
                # Requirements — URL Shortener ({{ctx.Scenario}})

                ## In scope
                - POST /shorten  -> creates a short code for a long URL
                - GET /{code}   -> redirects to the original URL, increments click count
                - GET /analytics/{code} -> returns click count + created/last-accessed timestamps
                - In-memory persistence (swappable for a real DB later)

                ## Acceptance criteria
                - POST /shorten with an absolute http(s) URL returns 200 and a 7-character code; any other scheme
                  (javascript:, data:, file:, ftp:) or a malformed URL returns 400.
                - GET /{code} returns 302 to the original URL and counts the click; an unknown code returns 404.
                - GET /analytics/{code} returns the click count and timestamps; an unknown code returns 404.
                - Under concurrent load no two URLs share a code and no clicks are lost.{{betterCriterion}}

                ## Decisions / ambiguity resolution
                {{decisions}}

                ## Explicit assumptions
                - Single-instance deployment (no distributed short-code coordination needed yet)
                - No authentication on v1 (flagged as a risk in the final summary)
                - No personal data is collected: no IP, user-agent or referrer tracking (enforced by policy CMP-001)
                """;

            await File.WriteAllTextAsync(Path.Combine(docsDir, "requirements.md"), content);
            ctx.Record("requirements.scenario", ctx.Scenario);
            return StageResult.Ok(IsBrownfield(ctx)
                ? "requirements.md + impact-analysis.md written"
                : "requirements.md written");
        },
        ExitGate = ctx =>
        {
            var text = File.ReadAllText(Path.Combine(DocsDir(ctx), "requirements.md"));
            var missing = new[] { "## In scope", "## Acceptance criteria", "## Explicit assumptions" }.Where(s => !text.Contains(s)).ToList();
            var findings = new List<PolicyFinding>
            {
                missing.Count == 0
                    ? PolicyFinding.Pass("REQ-001", "requirements define scope, acceptance criteria and assumptions")
                    : PolicyFinding.Block("REQ-001", $"requirements.md is missing: {string.Join(", ", missing)}")
            };
            if (IsBrownfield(ctx))
            {
                findings.Add(ctx.Get("brownfield.findingRules").Length > 0
                    ? PolicyFinding.Pass("REQ-002", $"impact analysis identified the change scope: {ctx.Get("brownfield.impactedFiles")}")
                    : PolicyFinding.Warn("REQ-002", "impact analysis found nothing to change - confirm the request is still needed"));
            }
            return GateResult.From(findings);
        }
    };

    public static StageDefinition Design() => new()
    {
        Name = "design",
        DependsOn = new[] { "requirements" },
        Execute = async ctx =>
        {
            var docsDir = DocsDir(ctx);
            Directory.CreateDirectory(docsDir);
            int revision = ctx.GetInt("design.revision") + 1;

            var content = $$"""
                # Design — URL Shortener API (revision {{revision}})

                ## Endpoints
                | Method | Path              | Request                | Response                          |
                |--------|-------------------|-------------------------|------------------------------------|
                | POST   | /shorten          | { "url": "..." }       | 200 { "code": "...", "shortUrl": "..." } / 400 |
                | GET    | /{code}           | -                       | 302 redirect to original URL / 404 |
                | GET    | /analytics/{code} | -                       | 200 { "clicks": n, "createdAt": "...", "lastAccessedAt": "..." } / 404 |

                ## Data model
                - ShortUrlEntry { Code (string, PK), OriginalUrl (string), CreatedAt, LastAccessedAt, ClickCount }
                - No personal data (IP, user-agent, referrer) is stored.

                ## Input validation
                - Only absolute http/https URLs with a host are accepted, so the redirect endpoint can never
                  serve javascript:, data: or file: targets.

                ## Short code generation
                - Base62-encoded random 7-character code from Random.Shared (thread-safe), reserved atomically with
                  ConcurrentDictionary.TryAdd; a collision retries generation (bounded at 10 attempts) instead of failing.

                ## Concurrency
                - Click count and last-accessed time are updated together under a per-entry lock; readers receive a
                  snapshot copy, so analytics never shows a count and timestamp out of step.

                ## Persistence
                - In-memory ConcurrentDictionary for this prototype; interface is small enough to
                  swap for EF Core + SQL later without touching the endpoint layer.
                """;

            if (IsBrownfield(ctx))
            {
                content += $"""


                    ## Brownfield change plan
                    - Change only: {ctx.Get("brownfield.impactedFiles")}. Keep unchanged: {ctx.Get("brownfield.unimpactedFiles")}
                      (routes and request/response shapes stay identical for existing clients).
                    - Resolve findings {ctx.Get("brownfield.findingRules")} as recommended in docs/brownfield/impact-analysis.md.
                    - Regression safety: existing endpoint behaviour is covered by HTTP integration tests that must pass
                      before release (CHG-002), and re-analysis must show every finding cleared (CHG-005).
                    """;
            }

            // A clarification raised later in the pipeline (see Testing) is folded
            // into the design on the re-planned pass, so the revision is visible
            // in the artifact rather than only in the audit log.
            bool clarified = ctx.Has("clarification.answer");
            if (clarified)
            {
                content += $"""


                    ## Clarifications applied
                    - Raised by: {ctx.Get("clarification.raisedBy")}
                    - Question: {ctx.Get("clarification.question")}
                    - Answer: {ctx.Get("clarification.answer")}
                    - Design impact: acceptance criteria for this release are the reliability behaviours above
                      (URL validation -> 400, unknown code -> 404, collision-safe generation, click tracking),
                      verified end-to-end through the HTTP API rather than only at the store layer.
                    """;
            }

            await File.WriteAllTextAsync(Path.Combine(docsDir, "design.md"), content);
            ctx.Record("design.revision", revision.ToString());
            ctx.Record("design.endpoints", string.Join(", ", Routes));

            return StageResult.Ok(clarified
                ? $"design.md revised with clarification (revision {revision})"
                : $"design.md written (revision {revision})");
        },
        ExitGate = ctx =>
        {
            var text = File.ReadAllText(Path.Combine(DocsDir(ctx), "design.md"));
            var missing = Routes.Where(r => !text.Contains(r)).ToList();
            return GateResult.From(missing.Count == 0
                ? PolicyFinding.Pass("DES-001", "design covers every in-scope endpoint")
                : PolicyFinding.Block("DES-001", $"design is missing endpoint(s): {string.Join(", ", missing)}"));
        }
    };

    public static StageDefinition Implementation() => new()
    {
        Name = "implementation",
        DependsOn = new[] { "design" },
        RequiresApproval = true, // high-impact: writes/overwrites generated source
        EntryGate = ctx => GateResult.From(ctx.Has("design.revision")
            ? PolicyFinding.Pass("DES-002", $"design revision {ctx.Get("design.revision")} is available to implement against")
            : PolicyFinding.Block("DES-002", "no design available - cannot implement without a design")),
        Execute = async ctx =>
        {
            var apiDir = ApiDir(ctx);
            Directory.CreateDirectory(apiDir);

            var files = new Dictionary<string, string>
            {
                ["UrlShortener.Api.csproj"] = ApiTemplates.ApiCsproj,
                ["Models.cs"] = ApiTemplates.ModelsCs,
                ["Store.cs"] = ApiTemplates.StoreCs,
                ["Program.cs"] = ApiTemplates.ProgramCs
            };

            string output;
            if (IsBrownfield(ctx))
            {
                // Check the existing code out, then patch only what needs to change.
                var baseline = BaselineDir(ctx);
                foreach (var src in Directory.EnumerateFiles(baseline))
                    File.Copy(src, Path.Combine(apiDir, Path.GetFileName(src)), overwrite: true);

                var changed = new List<string>();
                var diff = new StringBuilder();
                foreach (var (name, content) in files)
                {
                    var basePath = Path.Combine(baseline, name);
                    var old = File.Exists(basePath) ? await File.ReadAllTextAsync(basePath) : string.Empty;
                    if (LineDiff.AreEquivalent(old, content)) continue;

                    await File.WriteAllTextAsync(Path.Combine(apiDir, name), content);
                    changed.Add(name);
                    diff.Append(LineDiff.Unified(old, content, $"a/baseline/UrlShortener.Api/{name}", $"b/generated/UrlShortener.Api/{name}"));
                }

                Directory.CreateDirectory(BrownfieldDocsDir(ctx));
                await File.WriteAllTextAsync(Path.Combine(BrownfieldDocsDir(ctx), "changes.diff"), diff.ToString());
                ctx.Record("implementation.changedFiles", string.Join(",", changed));
                var unchanged = files.Keys.Except(changed);
                output = $"patched baseline: changed {string.Join(", ", changed)}; unchanged {string.Join(", ", unchanged)}; patch in docs/brownfield/changes.diff";
            }
            else
            {
                foreach (var (name, content) in files)
                    await File.WriteAllTextAsync(Path.Combine(apiDir, name), content);
                output = "UrlShortener.Api source generated (Program.cs, Models.cs, Store.cs, csproj)";
            }

            ctx.Record("implementation.basedOnDesignRevision", ctx.Get("design.revision"));
            return StageResult.Ok(output);
        },
        ExitGate = ctx =>
        {
            var apiDir = ApiDir(ctx);
            var sources = Directory.GetFiles(apiDir, "*.cs");
            var findings = new List<PolicyFinding>
            {
                Policies.NoHardcodedSecrets(sources, ctx.ProjectRoot),
                Policies.NoPiiInDataModel(sources, ctx.ProjectRoot)
            };

            if (IsBrownfield(ctx))
            {
                var changed = Split(ctx.Get("implementation.changedFiles"));
                var impacted = Split(ctx.Get("brownfield.impactedFiles"));
                findings.Add(Policies.ScopeContainment(changed, impacted));
                findings.Add(Policies.FixVerified(Split(ctx.Get("brownfield.findingRules")), CodebaseAnalyzer.Analyze(apiDir)));
            }
            return GateResult.From(findings);
        }
    };

    public static StageDefinition Testing() => new()
    {
        Name = "testing",
        DependsOn = new[] { "implementation" },
        Execute = async ctx =>
        {
            if (ctx.Scenario == "ambiguous" && !ctx.Has("clarification.answer"))
            {
                // Simulate: writing acceptance tests exposes that "make it better"
                // has no testable definition. The question goes to a stakeholder,
                // and the answer changes the design - so design and everything
                // built on it (implementation, testing, docs, release) is stale
                // and must be redone, even though implementation already ran.
                ctx.Record("clarification.raisedBy", "testing");
                ctx.Record("clarification.question", "'Make the URL shortener better' has no acceptance criterion - what does 'better' mean for this release?");
                ctx.Record("clarification.answer", "Reliability: correct validation, error codes and click tracking. No new features, no scale target.");
                return new StageResult
                {
                    Success = true,
                    Output = "requirement untestable as written; stakeholder clarified 'better' = reliability - re-planning from design",
                    ReplanFrom = "design"
                };
            }

            bool boosted = await WriteTestSuiteAsync(ctx);

            if (ctx.InjectedFaults.Contains("test-runner"))
                throw new InvalidOperationException("injected fault: test runner unavailable");

            var summary = await TestRunner.RunDotnetTestAsync(TestDir(ctx),
                Path.Combine(ctx.ProjectRoot, "artifacts", "test-results"), TimeSpan.FromMinutes(5));
            RecordEvidence(ctx, summary);
            return StageResult.Ok($"executed {summary.Total} test(s) via dotnet test ({(boosted ? "full" : "minimal")} suite): {summary.Passed} passed, {summary.Failed} failed");
        },
        Fallback = async ctx =>
        {
            // Test runner unavailable: still produce evidence (what tests exist),
            // clearly marked as unexecuted so release policies don't trust it as proof.
            await WriteTestSuiteAsync(ctx);
            var summary = TestRunner.StaticInventory(TestDir(ctx));
            RecordEvidence(ctx, summary);
            return StageResult.Ok($"inventoried {summary.Total} test(s) without executing them");
        },
        ExitGate = ctx => GateResult.From(ctx.GetInt("testing.testCount") > 0
            ? PolicyFinding.Pass("TST-001", $"{ctx.Get("testing.testCount")} test(s) discovered ({ctx.Get("testing.method")})")
            : PolicyFinding.Block("TST-001", "no tests discovered in the generated test project"))
    };

    public static StageDefinition Docs() => new()
    {
        Name = "docs",
        DependsOn = new[] { "implementation" },
        Execute = async ctx =>
        {
            var apiDir = ApiDir(ctx);
            Directory.CreateDirectory(apiDir);
            await File.WriteAllTextAsync(Path.Combine(apiDir, "README.md"), ApiTemplates.ApiReadme);
            return StageResult.Ok("API README.md written");
        },
        ExitGate = ctx =>
        {
            var text = File.ReadAllText(Path.Combine(ApiDir(ctx), "README.md"));
            var missing = Routes.Append("Known limitations").Where(s => !text.Contains(s)).ToList();
            return GateResult.From(missing.Count == 0
                ? PolicyFinding.Pass("DOC-001", "API README documents every endpoint and its known limitations")
                : PolicyFinding.Block("DOC-001", $"API README is missing: {string.Join(", ", missing)}"));
        }
    };

    public static StageDefinition Release() => new()
    {
        Name = "release",
        DependsOn = new[] { "testing", "docs" },
        RequiresApproval = true, // high-impact: marks the build release-ready
        EntryGate = ctx =>
        {
            var evidence = Policies.ReleaseTestEvidence(ctx);
            if (evidence.RollbackTarget is not null)
            {
                // Remediation decision: the re-run of Testing generates the full suite.
                ctx.Record("coverageBoost", "true");
                return evidence;
            }
            return GateResult.From(evidence.Findings.Prepend(Policies.ImplementationMatchesApprovedDesign(ctx)));
        },
        Execute = async ctx =>
        {
            var manifest = BuildManifest(ctx);
            ctx.Record("release.manifest", string.Join(";", manifest.Select(kv => $"{kv.Key}={kv.Value}")));

            var gateLines = StageOrder
                .SelectMany(s => new[] { "entry", "exit" }.Select(p => (Stage: s, Phase: p, Key: $"gate.{s}.{p}")))
                .Where(g => ctx.Has(g.Key))
                .Select(g => $"| {g.Stage} | {g.Phase} | {ctx.Get(g.Key)} |");

            var manifestLines = manifest.Select(kv => $"| `{kv.Key}` | `{kv.Value[..16]}…` |");

            var notes = $"""
                # Release Notes

                - Scenario: {ctx.Scenario}
                - Released at: {DateTimeOffset.UtcNow:o}
                - Design revision: {ctx.Get("design.revision")}

                ## Approvals
                - Implementation approved by: {ctx.Get("implementation.approvedBy")}
                - Release approved by: {ctx.Get("release.approvedBy")}

                ## Test evidence
                - Method: {ctx.Get("testing.method")}
                - Tests: {ctx.Get("testing.testCount")} total, {ctx.Get("testing.passed")} passed, {ctx.Get("testing.failed")} failed

                ## Policy results
                | Stage | Gate | Results |
                |-------|------|---------|
                {string.Join("\n", gateLines)}

                ## Artifact manifest (SHA-256, verified by the release exit gate)
                | File | SHA-256 |
                |------|---------|
                {string.Join("\n", manifestLines)}
                """;
            await File.WriteAllTextAsync(Path.Combine(DocsDir(ctx), "release-notes.md"), notes);
            return StageResult.Ok($"release notes written with {manifest.Count}-file manifest; build marked release-ready");
        },
        ExitGate = ctx =>
        {
            var recorded = ctx.Get("release.manifest").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Split('=', 2))
                .ToDictionary(p => p[0], p => p[1]);
            var current = BuildManifest(ctx);
            var drift = recorded.Where(kv => !current.TryGetValue(kv.Key, out var h) || h != kv.Value).Select(kv => kv.Key)
                .Concat(current.Keys.Except(recorded.Keys))
                .ToList();
            return GateResult.From(drift.Count == 0
                ? PolicyFinding.Pass("REL-001", $"artifact manifest verified ({current.Count} files unchanged since release notes were written)")
                : PolicyFinding.Block("REL-001", $"artifacts changed after being recorded in the manifest: {string.Join(", ", drift)}"));
        }
    };

    /// <summary>Writes the test project; returns whether the full (boosted) suite was written.</summary>
    private static async Task<bool> WriteTestSuiteAsync(PipelineContext ctx)
    {
        var testDir = TestDir(ctx);
        Directory.CreateDirectory(testDir);

        bool boosted = ctx.Has("coverageBoost");
        await File.WriteAllTextAsync(Path.Combine(testDir, "UrlShortener.Tests.csproj"), ApiTemplates.TestsCsproj);
        await File.WriteAllTextAsync(Path.Combine(testDir, "ShortenerTests.cs"), boosted ? ApiTemplates.TestsCsFull : ApiTemplates.TestsCsMinimal);

        // Integration tests are part of the boosted suite only. Remove any copy
        // left by a previous run so the minimal suite really is minimal.
        var integrationPath = Path.Combine(testDir, "ApiIntegrationTests.cs");
        if (boosted)
            await File.WriteAllTextAsync(integrationPath, ApiTemplates.IntegrationTestsCs);
        else
            File.Delete(integrationPath);

        return boosted;
    }

    private static void RecordEvidence(PipelineContext ctx, TestRunSummary summary)
    {
        ctx.Record("testing.executed", summary.Executed ? "true" : "false");
        ctx.Record("testing.method", summary.Method);
        ctx.Record("testing.testCount", summary.Total.ToString());
        ctx.Record("testing.passed", summary.Passed.ToString());
        ctx.Record("testing.failed", summary.Failed.ToString());
        ctx.Record("testing.passedTests", string.Join("|", summary.PassedTests.Distinct()));
        ctx.Record("testing.failedTests", string.Join("|", summary.FailedTests.Distinct()));
    }

    /// <summary>SHA-256 of every generated source/project/doc file, keyed by repo-relative path.</summary>
    private static SortedDictionary<string, string> BuildManifest(PipelineContext ctx)
    {
        var manifest = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(GeneratedDir(ctx), "*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}")) continue;
            if (Path.GetExtension(file) is not (".cs" or ".csproj" or ".md")) continue;
            var rel = Path.GetRelativePath(ctx.ProjectRoot, file).Replace('\\', '/');
            manifest[rel] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        }
        return manifest;
    }

    private static IReadOnlyCollection<string> Split(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
