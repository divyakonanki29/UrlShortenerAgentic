using System.Text.RegularExpressions;

namespace Orchestrator;

/// <summary>
/// Policy guardrails evaluated by stage gates. Each rule has a stable id so a
/// finding in audit.log or release-notes.md traces back to the rule text here.
///
///   SEC-001  No hardcoded secrets in generated source                (Block)
///   SEC-002  Redirect-target scheme allow-list verified by passing tests (Block)
///   CMP-001  No PII fields in the persisted data model               (Block)
///   CHG-001  Implementation built from the latest approved design    (Block)
///   CHG-002  Test evidence: >= 2 tests (else roll back), none failing (Block)
///   CHG-003  Validation evidence is degraded (tests not executed)    (Warn)
///   CHG-004  Brownfield change stays inside the analysed impact scope (Block)
///   CHG-005  Brownfield fix verified: re-analysis clears the findings (Block)
/// </summary>
public static partial class Policies
{
    public const int MinimumTests = 2;

    /// <summary>Tests that prove the SEC-002 control (non-http(s) redirect targets rejected) at unit and HTTP level.</summary>
    public static readonly string[] RequiredSecurityTests = { "Create_RejectsNonHttpSchemes", "Shorten_NonHttpScheme_Returns400" };

    public static PolicyFinding NoHardcodedSecrets(IEnumerable<string> files, string root)
    {
        var hits = ScanLines(files, root, SecretRegex());
        return hits.Count == 0
            ? PolicyFinding.Pass("SEC-001", "no hardcoded secrets found in generated source")
            : PolicyFinding.Block("SEC-001", $"possible hardcoded secret(s): {string.Join(", ", hits)}");
    }

    public static PolicyFinding NoPiiInDataModel(IEnumerable<string> files, string root)
    {
        var hits = ScanLines(files, root, PiiPropertyRegex());
        return hits.Count == 0
            ? PolicyFinding.Pass("CMP-001", "data model stores no PII (no IP/user-agent/referrer/contact/location fields)")
            : PolicyFinding.Block("CMP-001", $"PII field(s) in data model, which requirements exclude: {string.Join(", ", hits)}");
    }

    public static PolicyFinding ImplementationMatchesApprovedDesign(PipelineContext ctx)
    {
        int design = ctx.GetInt("design.revision");
        int built = ctx.GetInt("implementation.basedOnDesignRevision");
        return design > 0 && built == design
            ? PolicyFinding.Pass("CHG-001", $"implementation was approved and built against design revision {design} (the latest)")
            : PolicyFinding.Block("CHG-001", $"implementation was built against design revision {built}, but the latest design is revision {design}");
    }

    /// <summary>
    /// CHG-002/CHG-003/SEC-002 evaluated together on the Testing stage's evidence.
    /// Too few tests is recoverable (roll back to Testing so it can add coverage);
    /// failing tests or an unverified security control are not, and block.
    /// </summary>
    public static GateResult ReleaseTestEvidence(PipelineContext ctx)
    {
        int total = ctx.GetInt("testing.testCount");
        bool executed = ctx.Get("testing.executed") == "true";

        if (total < MinimumTests)
        {
            return new GateResult
            {
                Findings = new[] { PolicyFinding.Warn("CHG-002", $"only {total} test(s) present, minimum is {MinimumTests} - rolling back to testing for more coverage") },
                RollbackTarget = "testing"
            };
        }

        var findings = new List<PolicyFinding>();
        int failed = ctx.GetInt("testing.failed");
        findings.Add(failed > 0
            ? PolicyFinding.Block("CHG-002", $"{failed} test(s) failing: {ctx.Get("testing.failedTests")}")
            : PolicyFinding.Pass("CHG-002", executed ? $"{total} tests executed, all passing" : $"{total} tests present (not executed)"));

        if (!executed)
            findings.Add(PolicyFinding.Warn("CHG-003", $"test evidence is degraded: {ctx.Get("testing.method")}"));

        var passed = ctx.Get("testing.passedTests").Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var failedNames = ctx.Get("testing.failedTests").Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var unverified = RequiredSecurityTests.Where(t => !passed.Contains(t) || failedNames.Contains(t)).ToList();
        findings.Add(unverified.Count == 0
            ? PolicyFinding.Pass("SEC-002", "redirect scheme allow-list verified by passing tests")
            : PolicyFinding.Block("SEC-002", $"security control not verified by an executed, passing test: {string.Join(", ", unverified)}"));

        return GateResult.From(findings);
    }

    public static PolicyFinding ScopeContainment(IReadOnlyCollection<string> changedFiles, IReadOnlyCollection<string> impactedFiles)
    {
        var outOfScope = changedFiles.Except(impactedFiles).ToList();
        return outOfScope.Count == 0
            ? PolicyFinding.Pass("CHG-004", $"changed files ({Describe(changedFiles)}) are within the analysed impact scope ({Describe(impactedFiles)})")
            : PolicyFinding.Block("CHG-004", $"change touches file(s) outside the analysed impact scope: {string.Join(", ", outOfScope)}");
    }

    public static PolicyFinding FixVerified(IReadOnlyCollection<string> baselineRules, ImpactReport after)
    {
        var remaining = after.Findings.Where(f => baselineRules.Contains(f.RuleId)).ToList();
        return remaining.Count == 0
            ? PolicyFinding.Pass("CHG-005", $"re-analysis of the changed code clears all baseline findings ({string.Join(", ", baselineRules)})")
            : PolicyFinding.Block("CHG-005", $"finding(s) still present after the change: {string.Join(", ", remaining.Select(f => $"{f.RuleId} at {f.File}:{f.Line}"))}");
    }

    private static string Describe(IReadOnlyCollection<string> files) => files.Count == 0 ? "none" : string.Join(", ", files);

    private static List<string> ScanLines(IEnumerable<string> files, string root, Regex pattern)
    {
        var hits = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (pattern.IsMatch(lines[i]))
                    hits.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{i + 1}");
        }
        return hits;
    }

    // Assignments of string literals to secret-looking names, connection-string
    // credentials, and PEM private keys.
    [GeneratedRegex(@"(?i)((password|passwd|pwd|secret|api[_-]?key|access[_-]?token|client[_-]?secret)\w*""?\s*[:=]\s*""[^""]{4,}"")|(AccountKey=|Password=)[^;""\s]{4,}|-----BEGIN (RSA |EC )?PRIVATE KEY-----")]
    private static partial Regex SecretRegex();

    // Property declarations whose names indicate personal data.
    [GeneratedRegex(@"(?i)\b(ip|ipaddress|clientip|remoteip|useragent|referr?er|email|phone|phonenumber|latitude|longitude|geolocation)\s*\{\s*get;")]
    private static partial Regex PiiPropertyRegex();
}
