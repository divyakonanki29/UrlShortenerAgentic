using System.Text;
using System.Text.RegularExpressions;

namespace Orchestrator;

public record CodeFinding(string RuleId, string File, int Line, string Problem, string Recommendation);

public record EndpointInfo(string Method, string Route, string File, int Line, IReadOnlyList<string> StoreCalls);

public record ImpactReport(string Root, IReadOnlyList<string> Files, IReadOnlyList<EndpointInfo> Endpoints, IReadOnlyList<CodeFinding> Findings)
{
    public IReadOnlyList<string> ImpactedFiles => Findings.Select(f => f.File).Distinct().OrderBy(f => f).ToList();
    public IReadOnlyList<string> UnimpactedFiles => Files.Except(ImpactedFiles).OrderBy(f => f).ToList();
}

/// <summary>
/// Codebase reasoning for brownfield changes: reads an existing service's
/// source, inventories its API surface and data flow (endpoint -> store calls),
/// and flags reliability/security defects with file:line evidence. The rule set
/// is deliberately small and explicit so every finding is explainable; an LLM
/// reviewer could sit behind the same ImpactReport contract later.
/// </summary>
public static partial class CodebaseAnalyzer
{
    public static ImpactReport Analyze(string root)
    {
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f)
            .ToList();

        var endpoints = new List<EndpointInfo>();
        var findings = new List<CodeFinding>();

        foreach (var path in files)
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            var lines = File.ReadAllLines(path);
            var text = string.Join('\n', lines);

            endpoints.AddRange(FindEndpoints(rel, lines));

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                int lineNo = i + 1;

                if (SharedRandomRegex().IsMatch(line))
                    findings.Add(new CodeFinding("BF-001", rel, lineNo,
                        "A single static System.Random instance is shared by all requests; Random is not thread-safe and can return corrupted/repeated values under concurrency.",
                        "Use Random.Shared (thread-safe) for code generation."));

                if (ContainsKeyRegex().IsMatch(line) && HasIndexerAssignmentNear(lines, i))
                    findings.Add(new CodeFinding("BF-002", rel, lineNo,
                        "Check-then-act: the code is checked with ContainsKey and inserted later with an indexer assignment, so two concurrent requests can claim the same code and one silently overwrites the other.",
                        "Reserve the code atomically with ConcurrentDictionary.TryAdd and retry on failure (bounded)."));

                if (UnsynchronizedIncrementRegex().Match(line) is { Success: true } inc && !IsInsideLock(lines, i))
                    findings.Add(new CodeFinding("BF-003", rel, lineNo,
                        $"'{inc.Value.Trim().TrimEnd(';')}' mutates a shared entry without synchronization; concurrent redirects lose click updates, and readers can see the count and timestamp out of step.",
                        "Update click count and last-accessed time under a per-entry lock, and hand readers a snapshot."));
            }

            if (text.Contains("IsWellFormedUriString") && !text.Contains("UriSchemeHttp"))
            {
                int lineNo = Array.FindIndex(lines, l => l.Contains("IsWellFormedUriString")) + 1;
                findings.Add(new CodeFinding("BF-004", rel, lineNo,
                    "URL validation only checks that the URI is well-formed, so javascript:, data: and file: URIs are accepted and later served as redirect targets (open-redirect / script-injection risk).",
                    "Allow-list the http and https schemes (and require a host) before storing a URL."));
            }
        }

        var relFiles = files.Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).ToList();
        return new ImpactReport(root, relFiles, endpoints, findings);
    }

    private static IEnumerable<EndpointInfo> FindEndpoints(string rel, string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var m = EndpointRegex().Match(lines[i]);
            if (!m.Success) continue;

            // The handler body runs until the next endpoint mapping or app.Run().
            var calls = new List<string>();
            for (int j = i; j < lines.Length; j++)
            {
                if (j > i && (EndpointRegex().IsMatch(lines[j]) || lines[j].Contains("app.Run(")))
                    break;
                foreach (Match c in StoreCallRegex().Matches(lines[j]))
                    if (!calls.Contains(c.Groups[1].Value)) calls.Add(c.Groups[1].Value);
            }
            yield return new EndpointInfo(m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value, rel, i + 1, calls);
        }
    }

    private static bool HasIndexerAssignmentNear(string[] lines, int from)
    {
        for (int j = from; j < Math.Min(lines.Length, from + 10); j++)
            if (IndexerAssignRegex().IsMatch(lines[j])) return true;
        return false;
    }

    private static bool IsInsideLock(string[] lines, int index)
    {
        for (int j = index; j >= Math.Max(0, index - 3); j--)
            if (lines[j].Contains("lock (") || lines[j].Contains("lock(")) return true;
        return false;
    }

    public static string ToMarkdown(ImpactReport report, string displayRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Impact Analysis — {displayRoot}");
        sb.AppendLine();
        sb.AppendLine("Generated by the Requirements stage from static analysis of the existing code");
        sb.AppendLine("(`CodebaseAnalyzer`). Every finding cites the file and line it was found at.");
        sb.AppendLine();
        sb.AppendLine("## API surface and data flow");
        sb.AppendLine("| Method | Route | Defined at | Store calls (data flow) |");
        sb.AppendLine("|--------|-------|------------|--------------------------|");
        foreach (var e in report.Endpoints)
            sb.AppendLine($"| {e.Method} | `{e.Route}` | `{e.File}:{e.Line}` | {(e.StoreCalls.Count == 0 ? "-" : string.Join(" → ", e.StoreCalls.Select(c => $"`IUrlStore.{c}`")))} |");
        sb.AppendLine();
        sb.AppendLine("## Findings");
        if (report.Findings.Count == 0)
        {
            sb.AppendLine("None.");
        }
        else
        {
            sb.AppendLine("| Rule | Location | Problem | Recommended change |");
            sb.AppendLine("|------|----------|---------|--------------------|");
            foreach (var f in report.Findings)
                sb.AppendLine($"| {f.RuleId} | `{f.File}:{f.Line}` | {f.Problem} | {f.Recommendation} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Change scope");
        sb.AppendLine($"- **Impacted (may change):** {Join(report.ImpactedFiles)}");
        sb.AppendLine($"- **Not impacted (must not change):** {Join(report.UnimpactedFiles)}");
        sb.AppendLine("- Every endpoint above reaches the store through `IUrlStore`, so fixing the store");
        sb.AppendLine("  keeps the public API contract (routes, request/response shapes) unchanged.");
        sb.AppendLine("- The change-control gate (CHG-004) enforces this scope on the Implementation stage.");
        return sb.ToString();

        static string Join(IReadOnlyList<string> files) => files.Count == 0 ? "none" : string.Join(", ", files.Select(f => $"`{f}`"));
    }

    [GeneratedRegex(@"static\s+(?:readonly\s+)?Random\s+\w+\s*=\s*new")]
    private static partial Regex SharedRandomRegex();

    [GeneratedRegex(@"\.ContainsKey\(")]
    private static partial Regex ContainsKeyRegex();

    [GeneratedRegex(@"_\w+\[\w+\]\s*=[^=]")]
    private static partial Regex IndexerAssignRegex();

    [GeneratedRegex(@"\b\w+\.\w+\+\+\s*;")]
    private static partial Regex UnsynchronizedIncrementRegex();

    [GeneratedRegex(@"app\.Map(Get|Post|Put|Delete|Patch)\(""([^""]+)""")]
    private static partial Regex EndpointRegex();

    [GeneratedRegex(@"\bstore\.(\w+)\(")]
    private static partial Regex StoreCallRegex();
}
