using System.Globalization;
using System.Text;

namespace DogdouSpec.Core.Formatting;

/// <summary>Dependency-free, bounded-memory text comparison for review, without Git or filesystem writes.</summary>
public static class UnifiedDiff
{
    public static string Create(string path, string? before, string? after, bool normalizeLineEndings = true)
    {
        if (normalizeLineEndings)
        {
            before = before?.Replace("\r\n", "\n", StringComparison.Ordinal);
            after = after?.Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        if (before == after) return string.Empty;
        var left = Lines(before);
        var right = Lines(after);
        var edits = new List<Edit>();
        var prefix = 0;
        while (prefix < left.Count && prefix < right.Count && left[prefix] == right[prefix])
        {
            edits.Add(new Edit(' ', left[prefix++]));
        }
        var suffix = 0;
        while (suffix < left.Count - prefix && suffix < right.Count - prefix && left[^(suffix + 1)] == right[^(suffix + 1)]) suffix++;
        var leftCount = left.Count - prefix - suffix;
        var rightCount = right.Count - prefix - suffix;
        // Large replacements still produce a complete patch without quadratic memory growth.
        if ((long)(leftCount + 1) * (rightCount + 1) > 4_000_000)
        {
            for (var i = prefix; i < left.Count - suffix; i++) edits.Add(new Edit('-', left[i]));
            for (var i = prefix; i < right.Count - suffix; i++) edits.Add(new Edit('+', right[i]));
        }
        else
        {
            var lengths = new int[leftCount + 1, rightCount + 1];
            for (var i = leftCount - 1; i >= 0; i--)
                for (var j = rightCount - 1; j >= 0; j--)
                    lengths[i, j] = left[prefix + i] == right[prefix + j]
                        ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            var a = 0;
            var b = 0;
            while (a < leftCount || b < rightCount)
            {
                if (a < leftCount && b < rightCount && left[prefix + a] == right[prefix + b])
                {
                    edits.Add(new Edit(' ', left[prefix + a++]));
                    b++;
                }
                else if (a < leftCount && (b == rightCount || lengths[a + 1, b] >= lengths[a, b + 1]))
                    edits.Add(new Edit('-', left[prefix + a++]));
                else edits.Add(new Edit('+', right[prefix + b++]));
            }
        }
        for (var i = left.Count - suffix; i < left.Count; i++) edits.Add(new Edit(' ', left[i]));

        var output = new StringBuilder();
        output.Append("--- ").Append(before == null ? "/dev/null" : "local/" + path).Append('\n');
        output.Append("+++ ").Append(after == null ? "/dev/null" : "embedded/" + path).Append('\n');
        var oldLines = new int[edits.Count + 1];
        var newLines = new int[edits.Count + 1];
        for (var i = 0; i < edits.Count; i++)
        {
            oldLines[i + 1] = oldLines[i] + (edits[i].Kind == '+' ? 0 : 1);
            newLines[i + 1] = newLines[i] + (edits[i].Kind == '-' ? 0 : 1);
        }
        var position = 0;
        while (position < edits.Count)
        {
            while (position < edits.Count && edits[position].Kind == ' ') position++;
            if (position == edits.Count) break;
            var start = Math.Max(0, position - 3);
            var end = Math.Min(edits.Count, position + 4);
            for (var i = position + 1; i < Math.Min(edits.Count, end + 3); i++)
                if (edits[i].Kind != ' ') end = Math.Min(edits.Count, i + 4);
            var oldCount = oldLines[end] - oldLines[start];
            var newCount = newLines[end] - newLines[start];
            output.Append(CultureInfo.InvariantCulture, $"@@ -{oldLines[start] + (oldCount == 0 ? 0 : 1)},{oldCount} +{newLines[start] + (newCount == 0 ? 0 : 1)},{newCount} @@\n");
            for (var i = start; i < end; i++)
            {
                output.Append(edits[i].Kind).Append(edits[i].Line.Text).Append('\n');
                if (!edits[i].Line.Terminated) output.Append("\\ No newline at end of file\n");
            }
            position = end;
        }
        // A missing empty file and an existing empty file have no text hunks.
        if (edits.Count == 0) output.Append(before == null ? "# Empty file added\n" : "# Empty file removed\n");
        return output.ToString();
    }

    private static List<TextLine> Lines(string? text)
    {
        var result = new List<TextLine>();
        if (string.IsNullOrEmpty(text)) return result;
        var parts = text.Split('\n');
        for (var i = 0; i < parts.Length - 1; i++) result.Add(new TextLine(parts[i], true));
        if (parts[^1].Length > 0) result.Add(new TextLine(parts[^1], false));
        return result;
    }

    private sealed record TextLine(string Text, bool Terminated);
    private sealed record Edit(char Kind, TextLine Line);
}
