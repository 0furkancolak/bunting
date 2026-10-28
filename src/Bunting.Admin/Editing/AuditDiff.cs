using System.Text.Json;

namespace Bunting.Admin.Editing;

public enum DiffKind
{
    Same,
    Added,
    Removed,
}

public sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>Line diff of two JSON documents, pretty-printed so that property-level changes show up as single lines.</summary>
public static class AuditDiff
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static IReadOnlyList<DiffLine> Compare(JsonElement? before, JsonElement? after) =>
        Compare(Lines(before), Lines(after));

    public static IReadOnlyList<DiffLine> Compare(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        // Longest common subsequence; audit documents are small, so the quadratic table is fine.
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
        {
            for (var j = b.Count - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var result = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (a[x] == b[y])
            {
                result.Add(new DiffLine(DiffKind.Same, a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add(new DiffLine(DiffKind.Removed, a[x++]));
            }
            else
            {
                result.Add(new DiffLine(DiffKind.Added, b[y++]));
            }
        }

        while (x < a.Count)
        {
            result.Add(new DiffLine(DiffKind.Removed, a[x++]));
        }

        while (y < b.Count)
        {
            result.Add(new DiffLine(DiffKind.Added, b[y++]));
        }

        return result;
    }

    private static string[] Lines(JsonElement? element) =>
        element is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } e
            ? JsonSerializer.Serialize(e, Pretty).Split('\n').Select(l => l.TrimEnd('\r')).ToArray()
            : [];
}
