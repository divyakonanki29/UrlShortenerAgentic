using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Orchestrator;

/// <summary>
/// Evidence about the generated test suite. Executed=false means the tests were
/// only inventoried (static fallback), so Passed/Failed carry no information.
/// Test names are method names without class or theory arguments, e.g.
/// "Create_RejectsNonHttpSchemes"; a theory contributes one result per case.
/// </summary>
public record TestRunSummary(
    bool Executed,
    string Method,
    int Total,
    int Passed,
    int Failed,
    IReadOnlyList<string> PassedTests,
    IReadOnlyList<string> FailedTests,
    IReadOnlyList<string> DiscoveredTests);

public static partial class TestRunner
{
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>
    /// Runs `dotnet test` on the project and parses the TRX results file. Throws
    /// when no results are produced (build failure, missing SDK, timeout) - that
    /// is a runner fault, which the Testing stage retries and then falls back
    /// from. Failing tests are NOT a runner fault: they come back as Failed > 0
    /// and the release gate decides what to do.
    /// </summary>
    public static async Task<TestRunSummary> RunDotnetTestAsync(string testProjectDir, string resultsDir, TimeSpan timeout)
    {
        Directory.CreateDirectory(resultsDir);
        var trxName = $"results-{DateTime.UtcNow:yyyyMMddHHmmssfff}.trx";
        var psi = new ProcessStartInfo(ResolveDotnet())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in new[] { "test", testProjectDir, "--nologo", "--logger", $"trx;LogFileName={trxName}", "--results-directory", resultsDir })
            psi.ArgumentList.Add(arg);

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw new TimeoutException($"dotnet test did not finish within {timeout.TotalSeconds:F0}s");
        }

        var logPath = Path.Combine(resultsDir, Path.ChangeExtension(trxName, ".log"));
        await File.WriteAllTextAsync(logPath, output.ToString());

        var trxPath = Path.Combine(resultsDir, trxName);
        if (!File.Exists(trxPath))
        {
            var tail = string.Join(" / ", output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3).Select(l => l.Trim()));
            throw new InvalidOperationException($"dotnet test produced no results (exit code {process.ExitCode}); see {logPath}. Last output: {tail}");
        }

        return ParseTrx(trxPath);
    }

    public static TestRunSummary ParseTrx(string trxPath)
    {
        var doc = XDocument.Load(trxPath);
        var results = doc.Descendants(Trx + "UnitTestResult")
            .Select(r => (Name: ShortName((string?)r.Attribute("testName") ?? "?"), Outcome: (string?)r.Attribute("outcome") ?? "Unknown"))
            .ToList();

        var passed = results.Where(r => r.Outcome == "Passed").Select(r => r.Name).ToList();
        var failed = results.Where(r => r.Outcome != "Passed").Select(r => r.Name).ToList();
        return new TestRunSummary(true, "dotnet test", results.Count, passed.Count, failed.Count,
            passed, failed, results.Select(r => r.Name).Distinct().ToList());
    }

    /// <summary>
    /// Degraded fallback: inventories [Fact]/[Theory] methods in the test sources
    /// without running them. Good enough to keep the pipeline producing evidence,
    /// not good enough to prove behaviour - which is why the release policies
    /// treat it as unverified.
    /// </summary>
    public static TestRunSummary StaticInventory(string testProjectDir)
    {
        var names = new List<string>();
        foreach (var file in Directory.EnumerateFiles(testProjectDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            foreach (Match m in TestMethodRegex().Matches(File.ReadAllText(file)))
                names.Add(m.Groups["name"].Value);
        }
        return new TestRunSummary(false, "static inventory (tests not executed)", names.Count, 0, 0,
            Array.Empty<string>(), Array.Empty<string>(), names);
    }

    /// <summary>"Ns.Class.Method(arg: \"a.b\")" -> "Method"</summary>
    public static string ShortName(string testName)
    {
        var paren = testName.IndexOf('(');
        var withoutArgs = paren >= 0 ? testName[..paren] : testName;
        var dot = withoutArgs.LastIndexOf('.');
        return dot >= 0 ? withoutArgs[(dot + 1)..] : withoutArgs;
    }

    /// <summary>
    /// The orchestrator may be launched without dotnet on PATH (e.g. via the full
    /// path to dotnet.exe), so prefer the host that `dotnet run` reports.
    /// </summary>
    public static string ResolveDotnet()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT") is { } root ? Path.Combine(root, exe) : null,
            OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", exe) : "/usr/share/dotnet/dotnet"
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c)) ?? "dotnet";
    }

    [GeneratedRegex(@"\[(?:Fact|Theory)\][\s\S]*?public\s+(?:async\s+Task|void)\s+(?<name>\w+)\s*\(")]
    private static partial Regex TestMethodRegex();
}
