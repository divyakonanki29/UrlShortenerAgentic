namespace Orchestrator;

/// <summary>
/// Human approval via the console. Shows the policy warnings the reviewer is
/// being asked to accept, and records who approved so the audit trail carries
/// a reviewer identity rather than an anonymous "y".
/// </summary>
public class ConsoleApprover
{
    private readonly string _reviewer;
    private readonly object _consoleLock = new();

    public ConsoleApprover(string reviewer) => _reviewer = reviewer;

    public ApprovalDecision Approve(ApprovalRequest request)
    {
        lock (_consoleLock)
        {
            Console.WriteLine();
            Console.WriteLine($"[APPROVAL REQUIRED] Stage '{request.Stage}' is a high-impact action (reviewer: {_reviewer}).");
            foreach (var w in request.Warnings)
                Console.WriteLine($"  ! {w}");
            Console.Write(request.Warnings.Count == 0
                ? "Approve? (y/n): "
                : $"Approve and accept {request.Warnings.Count} warning(s)? (y/n): ");

            // Input redirected from Windows PowerShell can carry a UTF-8 BOM; it is
            // not part of the answer, so ignore it. Anything other than "y" rejects.
            var input = Console.ReadLine()?.Trim().TrimStart('﻿');
            bool approved = string.Equals(input, "y", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine(approved ? "  -> approved" : "  -> rejected");
            return new ApprovalDecision(approved, _reviewer);
        }
    }
}
