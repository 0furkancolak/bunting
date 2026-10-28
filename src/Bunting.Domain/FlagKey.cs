using System.Text.RegularExpressions;

namespace Bunting.Domain;

public static partial class FlagKey
{
    public const string Pattern = "^[a-z0-9][a-z0-9._-]{0,99}$";

    public static bool IsValid(string? key) => key is not null && Regex().IsMatch(key);

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex Regex();
}
