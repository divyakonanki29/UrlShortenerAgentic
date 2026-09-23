using System.Text;

namespace Orchestrator;

/// <summary>
/// Minimal unified diff (LCS-based) so brownfield changes are reviewable as a
/// patch against the existing code rather than as whole-file rewrites. Line
/// endings and trailing whitespace are normalised so only real edits show.
/// </summary>
public static class LineDiff
{
    public static bool AreEquivalent(string a, string b) => Normalise(a).SequenceEqual(Normalise(b));

    public static string Unified(string oldText, string newText, string oldLabel, string newLabel, int context = 3)
    {
        var a = Normalise(oldText);
        var b = Normalise(newText);
        int n = a.Length, m = b.Length;

        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        // (op, text, index in a, index in b) where the index is the position *before* the op applies.
        var ops = new List<(char Op, string Text, int A, int B)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { ops.Add((' ', a[x], x, y)); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { ops.Add(('-', a[x], x, y)); x++; }
            else { ops.Add(('+', b[y], x, y)); y++; }
        }
        while (x < n) { ops.Add(('-', a[x], x, y)); x++; }
        while (y < m) { ops.Add(('+', b[y], x, y)); y++; }

        if (ops.All(o => o.Op == ' ')) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"--- {oldLabel}");
        sb.AppendLine($"+++ {newLabel}");

        int k = 0;
        while (k < ops.Count)
        {
            int firstChange = ops.FindIndex(k, o => o.Op != ' ');
            if (firstChange < 0) break;

            // Merge changes separated by at most 2*context unchanged lines into one hunk.
            int lastChange = firstChange, scan = firstChange + 1;
            while (scan < ops.Count)
            {
                if (ops[scan].Op != ' ') { lastChange = scan; scan++; continue; }
                int run = scan;
                while (run < ops.Count && ops[run].Op == ' ') run++;
                if (run < ops.Count && run - scan <= 2 * context) { scan = run; continue; }
                break;
            }

            int start = Math.Max(k, firstChange - context);
            int end = Math.Min(ops.Count - 1, lastChange + context);
            var hunk = ops.GetRange(start, end - start + 1);
            int oldCount = hunk.Count(o => o.Op != '+');
            int newCount = hunk.Count(o => o.Op != '-');
            sb.AppendLine($"@@ -{hunk[0].A + 1},{oldCount} +{hunk[0].B + 1},{newCount} @@");
            foreach (var o in hunk)
                sb.AppendLine($"{o.Op}{o.Text}");
            k = end + 1;
        }

        return sb.ToString();
    }

    private static string[] Normalise(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.ToArray();
    }
}
