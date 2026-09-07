using System.Text;

namespace AspireUI.Server.Services;

/// <summary>
/// A unified diff of two texts. Written out because the one thing it is needed for — showing what a
/// redeploy would change in a compose file — is not worth a dependency, and a diff of two files that
/// are ninety percent the same is a well-understood forty lines.
/// </summary>
public static class TextDiff
{
    public record Result(bool Changed, int Added, int Removed, string Text);

    public static Result Unified(string before, string after, int context = 3)
    {
        var a = Split(before);
        var b = Split(after);
        var lcs = LongestCommonSubsequence(a, b);

        // Walk both sides in step with the common subsequence: what is not common is a change.
        var ops = new List<(char Kind, string Line)>();
        int i = 0, j = 0;
        foreach (var (ai, bi) in lcs)
        {
            while (i < ai) ops.Add(('-', a[i++]));
            while (j < bi) ops.Add(('+', b[j++]));
            ops.Add((' ', a[i])); i++; j++;
        }
        while (i < a.Length) ops.Add(('-', a[i++]));
        while (j < b.Length) ops.Add(('+', b[j++]));

        var added = ops.Count(o => o.Kind == '+');
        var removed = ops.Count(o => o.Kind == '-');
        if (added == 0 && removed == 0) return new Result(false, 0, 0, "");

        // Only the neighbourhood of a change is interesting; the rest is collapsed to a marker.
        var keep = new bool[ops.Count];
        for (var k = 0; k < ops.Count; k++)
        {
            if (ops[k].Kind == ' ') continue;
            for (var c = Math.Max(0, k - context); c <= Math.Min(ops.Count - 1, k + context); c++) keep[c] = true;
        }

        var sb = new StringBuilder();
        var skipping = false;
        for (var k = 0; k < ops.Count; k++)
        {
            if (!keep[k])
            {
                if (!skipping) { sb.AppendLine("@@"); skipping = true; }
                continue;
            }
            skipping = false;
            sb.AppendLine(ops[k].Kind + ops[k].Line);
        }
        return new Result(true, added, removed, sb.ToString().TrimEnd('\n', '\r'));
    }

    private static string[] Split(string text) =>
        text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    /// <summary>
    /// Index pairs of the longest common subsequence. The classic table: fine for two compose files,
    /// and the inputs here are hundreds of lines, not millions.
    /// </summary>
    private static List<(int A, int B)> LongestCommonSubsequence(string[] a, string[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);

        var pairs = new List<(int, int)>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { pairs.Add((x, y)); x++; y++; }
            else if (table[x + 1, y] >= table[x, y + 1]) x++;
            else y++;
        }
        return pairs;
    }
}
