using System.Globalization;

namespace Bunting.Evaluation;

/// <summary>
/// Semantic version (semver.org 2.0 precedence). One to three numeric components are accepted
/// (<c>1.2</c> is <c>1.2.0</c>); build metadata is ignored. A leading "v" is not accepted.
/// </summary>
internal readonly struct SemVer : IComparable<SemVer>
{
    private SemVer(long major, long minor, long patch, string? pre)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = pre;
    }

    public long Major { get; }

    public long Minor { get; }

    public long Patch { get; }

    public string? PreRelease { get; }

    public static bool TryParse(ReadOnlySpan<char> text, out SemVer version)
    {
        version = default;
        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        string? pre = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            var preSpan = text[(dash + 1)..];
            if (!IsValidPreRelease(preSpan))
            {
                return false;
            }

            pre = preSpan.ToString();
            text = text[..dash];
        }

        Span<long> parts = stackalloc long[3];
        var count = 0;
        foreach (var range in text.Split('.'))
        {
            if (count == 3 || !TryParseNumeric(text[range], out parts[count]))
            {
                return false;
            }

            count++;
        }

        if (count == 0)
        {
            return false;
        }

        version = new SemVer(parts[0], parts[1], parts[2], pre);
        return true;
    }

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }

        c = Minor.CompareTo(other.Minor);
        if (c != 0)
        {
            return c;
        }

        c = Patch.CompareTo(other.Patch);
        if (c != 0)
        {
            return c;
        }

        if (PreRelease is null)
        {
            return other.PreRelease is null ? 0 : 1;
        }

        return other.PreRelease is null ? -1 : ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        while (true)
        {
            var aDot = a.IndexOf('.');
            var bDot = b.IndexOf('.');
            var aId = aDot < 0 ? a : a[..aDot];
            var bId = bDot < 0 ? b : b[..bDot];

            var aNum = IsNumeric(aId);
            var bNum = IsNumeric(bId);
            int c;
            if (aNum && bNum)
            {
                c = aId.Length != bId.Length ? aId.Length.CompareTo(bId.Length) : aId.SequenceCompareTo(bId);
            }
            else if (aNum != bNum)
            {
                c = aNum ? -1 : 1; // numeric identifiers have lower precedence
            }
            else
            {
                c = aId.SequenceCompareTo(bId);
            }

            if (c != 0)
            {
                return Math.Sign(c);
            }

            if (aDot < 0 || bDot < 0)
            {
                // The set with more identifiers has higher precedence.
                return (aDot < 0 ? 0 : 1) - (bDot < 0 ? 0 : 1);
            }

            a = a[(aDot + 1)..];
            b = b[(bDot + 1)..];
        }
    }

    private static bool IsNumeric(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty)
        {
            return false;
        }

        foreach (var ch in s)
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseNumeric(ReadOnlySpan<char> s, out long value)
    {
        value = 0;
        return IsNumeric(s) && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsValidPreRelease(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty)
        {
            return false;
        }

        foreach (var range in s.Split('.'))
        {
            var id = s[range];
            if (id.IsEmpty)
            {
                return false;
            }

            foreach (var ch in id)
            {
                if (!(char.IsAsciiLetterOrDigit(ch) || ch == '-'))
                {
                    return false;
                }
            }

            if (IsNumeric(id) && id.Length > 1 && id[0] == '0')
            {
                return false;
            }
        }

        return true;
    }
}
