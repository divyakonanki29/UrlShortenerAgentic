using Orchestrator;
using Xunit;

namespace Orchestrator.Tests;

public class PolicyAndAnalysisTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("policy-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "orchestrator", "Orchestrator.csproj"))) return dir.FullName;
        throw new InvalidOperationException("repo root not found");
    }

    // ---- SEC-001 / CMP-001 ----

    [Theory]
    [InlineData("var apiKey = \"sk-live-1234567890\";")]
    [InlineData("const string Password = \"hunter22\";")]
    [InlineData("\"Server=db;User Id=sa;Password=Sup3rSecret;\"")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    public void Secret_scan_blocks_hardcoded_credentials(string line)
    {
        var file = WriteFile("Leaky.cs", $"class C {{\n  {line}\n}}");
        var finding = Policies.NoHardcodedSecrets(new[] { file }, _dir);
        Assert.Equal(PolicySeverity.Block, finding.Severity);
        Assert.Contains("Leaky.cs:2", finding.Message);
    }

    [Fact]
    public void Secret_scan_passes_the_generated_api_source()
    {
        var files = new[] { WriteFile("Store.cs", ApiTemplates.StoreCs), WriteFile("Program.cs", ApiTemplates.ProgramCs), WriteFile("Models.cs", ApiTemplates.ModelsCs) };
        Assert.Equal(PolicySeverity.Pass, Policies.NoHardcodedSecrets(files, _dir).Severity);
        Assert.Equal(PolicySeverity.Pass, Policies.NoPiiInDataModel(files, _dir).Severity);
    }

    [Fact]
    public void Pii_scan_blocks_personal_data_fields()
    {
        var file = WriteFile("Models.cs", "public class Click { public string IpAddress { get; set; } = \"\"; public string UserAgent { get; set; } = \"\"; }");
        var finding = Policies.NoPiiInDataModel(new[] { file }, _dir);
        Assert.Equal(PolicySeverity.Block, finding.Severity);
    }

    // ---- CHG-001 / CHG-002 / CHG-003 / SEC-002 ----

    private static PipelineContext Evidence(int total, int failed, bool executed, string passedTests, string failedTests = "")
    {
        var ctx = new PipelineContext();
        ctx.Record("testing.testCount", total.ToString());
        ctx.Record("testing.failed", failed.ToString());
        ctx.Record("testing.executed", executed ? "true" : "false");
        ctx.Record("testing.method", executed ? "dotnet test" : "static inventory (tests not executed)");
        ctx.Record("testing.passedTests", passedTests);
        ctx.Record("testing.failedTests", failedTests);
        return ctx;
    }

    private const string SecurityTests = "Create_RejectsNonHttpSchemes|Shorten_NonHttpScheme_Returns400";

    [Fact]
    public void Too_few_tests_rolls_back_to_testing()
    {
        var gate = Policies.ReleaseTestEvidence(Evidence(1, 0, true, "Create_ReturnsEntryWithMatchingUrl"));
        Assert.Equal("testing", gate.RollbackTarget);
        Assert.False(gate.IsBlocked);
    }

    [Fact]
    public void Full_passing_evidence_passes_every_check()
    {
        var gate = Policies.ReleaseTestEvidence(Evidence(17, 0, true, SecurityTests + "|Other"));
        Assert.Null(gate.RollbackTarget);
        Assert.All(gate.Findings, f => Assert.Equal(PolicySeverity.Pass, f.Severity));
    }

    [Fact]
    public void Failing_tests_block_release()
    {
        var gate = Policies.ReleaseTestEvidence(Evidence(17, 1, true, SecurityTests, "Redirect_KnownCode_RedirectsToOriginalUrl"));
        Assert.Contains(gate.Findings, f => f.PolicyId == "CHG-002" && f.Severity == PolicySeverity.Block);
    }

    [Fact]
    public void Failing_security_test_blocks_even_if_another_case_passed()
    {
        var gate = Policies.ReleaseTestEvidence(Evidence(17, 1, true, SecurityTests, "Create_RejectsNonHttpSchemes"));
        Assert.Contains(gate.Findings, f => f.PolicyId == "SEC-002" && f.Severity == PolicySeverity.Block);
    }

    [Fact]
    public void Unexecuted_tests_warn_and_leave_the_security_control_unverified()
    {
        var gate = Policies.ReleaseTestEvidence(Evidence(17, 0, false, ""));
        Assert.Contains(gate.Findings, f => f.PolicyId == "CHG-003" && f.Severity == PolicySeverity.Warn);
        Assert.Contains(gate.Findings, f => f.PolicyId == "SEC-002" && f.Severity == PolicySeverity.Block);
    }

    [Fact]
    public void Implementation_built_on_stale_design_is_blocked()
    {
        var ctx = new PipelineContext();
        ctx.Record("design.revision", "2");
        ctx.Record("implementation.basedOnDesignRevision", "1");
        Assert.Equal(PolicySeverity.Block, Policies.ImplementationMatchesApprovedDesign(ctx).Severity);

        ctx.Record("implementation.basedOnDesignRevision", "2");
        Assert.Equal(PolicySeverity.Pass, Policies.ImplementationMatchesApprovedDesign(ctx).Severity);
    }

    // ---- CHG-004 ----

    [Fact]
    public void Scope_containment_blocks_out_of_scope_edits()
    {
        Assert.Equal(PolicySeverity.Pass, Policies.ScopeContainment(new[] { "Store.cs" }, new[] { "Store.cs" }).Severity);
        var finding = Policies.ScopeContainment(new[] { "Store.cs", "Program.cs" }, new[] { "Store.cs" });
        Assert.Equal(PolicySeverity.Block, finding.Severity);
        Assert.Contains("Program.cs", finding.Message);
    }

    // ---- Codebase analysis / CHG-005 ----

    [Fact]
    public void Analyzer_finds_the_baseline_defects_with_locations()
    {
        var report = CodebaseAnalyzer.Analyze(Path.Combine(RepoRoot(), "baseline", "UrlShortener.Api"));

        Assert.Equal(new[] { "BF-001", "BF-002", "BF-003", "BF-004" }, report.Findings.Select(f => f.RuleId).Distinct().OrderBy(r => r));
        Assert.All(report.Findings, f => Assert.Equal("Store.cs", f.File));
        Assert.Equal(new[] { "Store.cs" }, report.ImpactedFiles);
        Assert.Contains("Program.cs", report.UnimpactedFiles);

        Assert.Equal(3, report.Endpoints.Count);
        var redirect = Assert.Single(report.Endpoints, e => e.Route == "/{code}");
        Assert.Equal(new[] { "Get", "RecordClick" }, redirect.StoreCalls);
    }

    [Fact]
    public void Analyzer_reports_nothing_on_the_fixed_store_so_the_fix_is_verified()
    {
        WriteFile("Store.cs", ApiTemplates.StoreCs);
        WriteFile("Program.cs", ApiTemplates.ProgramCs);
        var after = CodebaseAnalyzer.Analyze(_dir);

        Assert.Empty(after.Findings);
        Assert.Equal(PolicySeverity.Pass, Policies.FixVerified(new[] { "BF-001", "BF-002", "BF-003", "BF-004" }, after).Severity);
    }

    [Fact]
    public void Brownfield_patch_leaves_program_and_models_identical_to_baseline()
    {
        var baseline = Path.Combine(RepoRoot(), "baseline", "UrlShortener.Api");
        Assert.True(LineDiff.AreEquivalent(File.ReadAllText(Path.Combine(baseline, "Program.cs")), ApiTemplates.ProgramCs));
        Assert.True(LineDiff.AreEquivalent(File.ReadAllText(Path.Combine(baseline, "Models.cs")), ApiTemplates.ModelsCs));
        Assert.False(LineDiff.AreEquivalent(File.ReadAllText(Path.Combine(baseline, "Store.cs")), ApiTemplates.StoreCs));
    }

    // ---- LineDiff ----

    [Fact]
    public void Diff_is_empty_for_equivalent_text_and_shows_changed_lines()
    {
        Assert.Equal(string.Empty, LineDiff.Unified("a\r\nb\n", "a\nb", "old", "new"));

        var diff = LineDiff.Unified("one\ntwo\nthree\n", "one\n2\nthree\n", "old", "new");
        Assert.Contains("@@ -1,3 +1,3 @@", diff);
        Assert.Contains("-two", diff);
        Assert.Contains("+2", diff);
    }

    // ---- TestRunner ----

    [Fact]
    public void Trx_parsing_counts_each_theory_case_and_strips_arguments()
    {
        var trx = WriteFile("r.trx", """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="UrlShortener.Tests.ShortenerTests.Create_RejectsNonHttpSchemes(url: &quot;file:///etc/passwd&quot;)" outcome="Passed" />
                <UnitTestResult testName="UrlShortener.Tests.ShortenerTests.Create_RejectsNonHttpSchemes(url: &quot;javascript:alert(1)&quot;)" outcome="Passed" />
                <UnitTestResult testName="UrlShortener.Tests.ApiIntegrationTests.Redirect_UnknownCode_Returns404" outcome="Failed" />
              </Results>
            </TestRun>
            """);
        var summary = TestRunner.ParseTrx(trx);

        Assert.Equal(3, summary.Total);
        Assert.Equal(2, summary.Passed);
        Assert.Equal(new[] { "Redirect_UnknownCode_Returns404" }, summary.FailedTests);
        Assert.All(summary.PassedTests, n => Assert.Equal("Create_RejectsNonHttpSchemes", n));
    }

    [Fact]
    public void Static_inventory_finds_facts_and_theories_without_executing()
    {
        WriteFile("ShortenerTests.cs", ApiTemplates.TestsCsFull);
        WriteFile("ApiIntegrationTests.cs", ApiTemplates.IntegrationTestsCs);
        var summary = TestRunner.StaticInventory(_dir);

        Assert.False(summary.Executed);
        Assert.Equal(0, summary.Passed);
        Assert.Contains("Create_RejectsNonHttpSchemes", summary.DiscoveredTests);
        Assert.Contains("Shorten_NonHttpScheme_Returns400", summary.DiscoveredTests);
    }
}
