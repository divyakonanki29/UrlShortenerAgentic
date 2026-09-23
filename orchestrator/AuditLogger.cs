using System.Text.Json;

namespace Orchestrator;

/// <summary>
/// Append-only, audit-grade log. Every entry is one JSON line so the file
/// stays diffable and greppable, and can be replayed to reconstruct exactly
/// what the pipeline did and why (decision lineage).
/// </summary>
public class AuditLogger
{
    private readonly string _path;
    private readonly object _lock = new();

    public AuditLogger(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, string.Empty);
    }

    public void Log(string stage, string eventType, string details)
    {
        var entry = new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("o"),
            stage,
            eventType,
            details
        };
        var line = JsonSerializer.Serialize(entry);
        lock (_lock)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }
}
