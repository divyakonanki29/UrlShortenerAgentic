using Orchestrator;

var scenario = "greenfield";
var reviewer = Environment.UserName;
var faults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var arg in args)
{
    if (arg.StartsWith("--scenario="))
        scenario = arg["--scenario=".Length..];
    else if (arg.StartsWith("--reviewer="))
        reviewer = arg["--reviewer=".Length..];
    else if (arg.StartsWith("--inject-fault="))
        faults.UnionWith(arg["--inject-fault=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

if (scenario is not ("greenfield" or "brownfield" or "ambiguous"))
{
    Console.WriteLine($"Unknown scenario '{scenario}', defaulting to greenfield.");
    scenario = "greenfield";
}

var projectRoot = FindProjectRoot();

var context = new PipelineContext { Scenario = scenario, ProjectRoot = projectRoot, InjectedFaults = faults };
var audit = new AuditLogger(Path.Combine(projectRoot, "audit.log"));
var approver = new ConsoleApprover(reviewer);
var engine = new PipelineEngine(context, audit, approver.Approve);

engine.Register(Stages.Requirements());
engine.Register(Stages.Design());
engine.Register(Stages.Implementation());
engine.Register(Stages.Testing());
engine.Register(Stages.Docs());
engine.Register(Stages.Release());

Console.WriteLine($"Running agentic SDLC pipeline — scenario: {scenario}");
Console.WriteLine($"Project root: {projectRoot}");
Console.WriteLine($"Reviewer:     {reviewer}");
if (faults.Count > 0)
    Console.WriteLine($"Injected faults: {string.Join(", ", faults)}");
Console.WriteLine("High-impact stages (implementation, release) require your approval.");

await engine.RunAsync();

engine.Metrics.PrintSummary();
engine.Metrics.WriteJson(Path.Combine(projectRoot, "metrics.json"));

Console.WriteLine();
Console.WriteLine($"Audit trail: {Path.Combine(projectRoot, "audit.log")}");
Console.WriteLine($"Metrics:     {Path.Combine(projectRoot, "metrics.json")}");
Console.WriteLine($"Generated:   {Path.Combine(projectRoot, "generated")}");

// Non-zero exit when the pipeline did not complete, so CI can gate on it.
Environment.ExitCode = engine.Metrics.Summarize().Outcome == "complete" ? 0 : 1;

// The repo root is the folder containing orchestrator/Orchestrator.csproj. Search
// up from the binary first (independent of the caller's working directory), then
// from the working directory.
static string FindProjectRoot()
{
    foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "orchestrator", "Orchestrator.csproj")))
                return dir.FullName;
        }
    }
    throw new InvalidOperationException("Could not find the project root (a folder containing orchestrator/Orchestrator.csproj).");
}
