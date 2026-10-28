using System.Globalization;
using System.Text.RegularExpressions;
using Bunting.Domain;

namespace Bunting.Evaluation;

/// <summary>A condition with all operands parsed up-front so that evaluation never parses configuration.</summary>
internal sealed class CompiledCondition
{
    private const int MaxSegmentDepth = 8;
    private const string CountryAttribute = "country";

    private readonly ConditionOperator _operator;
    private readonly bool _negate;
    private readonly string _attribute;
    private readonly bool _useTargetingKey;
    private readonly StringComparison _comparison;
    private readonly string[] _strings;
    private readonly double[] _numbers;
    private readonly double _number;
    private readonly SemVer _semVer;
    private readonly DateTimeOffset _date;
    private readonly Regex? _regex;
    private readonly bool _uppercaseInput;

    public CompiledCondition(
        Condition source,
        IReadOnlyDictionary<string, CompiledSegment> segments,
        Action<string> warn)
    {
        _operator = source.Operator;
        _negate = source.Negate;
        _attribute = source.Attribute ?? string.Empty;
        _useTargetingKey = _attribute == EvaluationContext.TargetingKeyAttribute;
        var isCountry = _attribute == CountryAttribute;
        _comparison = isCountry ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _uppercaseInput = isCountry;
        _strings = [.. source.Values ?? []];
        _numbers = [];
        Segments = [];

        if (_operator == ConditionOperator.InSegment)
        {
            var resolved = new List<CompiledSegment>();
            foreach (var key in _strings)
            {
                if (segments.TryGetValue(key, out var segment))
                {
                    resolved.Add(segment);
                }
                else
                {
                    warn($"Condition references unknown segment '{key}'.");
                }
            }

            Segments = [.. resolved];
            return;
        }

        if (string.IsNullOrEmpty(_attribute))
        {
            warn($"Condition with operator {_operator} has no attribute and can never match.");
            Never = true;
            return;
        }

        if (_strings.Length == 0)
        {
            warn($"Condition on '{_attribute}' with operator {_operator} has no values and can never match.");
            Never = true;
            return;
        }

        switch (_operator)
        {
            case ConditionOperator.Equals:
            case ConditionOperator.In:
                _numbers = new double[_strings.Length];
                for (var i = 0; i < _strings.Length; i++)
                {
                    _numbers[i] = TryParseDouble(_strings[i], out var d) ? d : double.NaN;
                }

                break;
            case ConditionOperator.GreaterThan:
            case ConditionOperator.LessThan:
                if (!TryParseDouble(_strings[0], out _number))
                {
                    Invalid($"'{_strings[0]}' is not a number.", warn);
                }

                break;
            case ConditionOperator.SemVerGreaterThan:
            case ConditionOperator.SemVerLessThan:
            case ConditionOperator.SemVerEquals:
                if (!SemVer.TryParse(_strings[0], out _semVer))
                {
                    Invalid($"'{_strings[0]}' is not a semantic version.", warn);
                }

                break;
            case ConditionOperator.Before:
            case ConditionOperator.After:
                if (!TryParseDate(_strings[0], out _date))
                {
                    Invalid($"'{_strings[0]}' is not an ISO 8601 date.", warn);
                }

                break;
            case ConditionOperator.Matches:
                _regex = RegexCache.Get(_strings[0]);
                if (_regex is null)
                {
                    Invalid($"'{_strings[0]}' is not a valid regular expression.", warn);
                }

                break;
            case ConditionOperator.Contains:
            case ConditionOperator.StartsWith:
            case ConditionOperator.EndsWith:
                break;
            default:
                Invalid($"Unsupported operator {_operator}.", warn);
                break;
        }
    }

    public CompiledSegment[] Segments { get; }

    public bool Never { get; private set; }

    public bool Evaluate(EvaluationContext context, int depth)
    {
        if (Never)
        {
            return false;
        }

        if (_operator == ConditionOperator.InSegment)
        {
            // Evaluable by definition: membership is always decidable, so Negate ("not in segment") works
            // even when the context carries no targeting key.
            var member = false;
            foreach (var segment in Segments)
            {
                if (segment.IsMember(context, depth + 1))
                {
                    member = true;
                    break;
                }
            }

            return member != _negate;
        }

        object? value = _useTargetingKey ? context.TargetingKey : context.GetAttribute(_attribute);

        // Missing attribute, or operand of an incompatible type: not evaluable, never matches (even if negated).
        return value is not null && TryTest(value, out var result) && result != _negate;
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static bool TryParseDate(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);

    private static bool TryGetDouble(object value, out double result)
    {
        switch (value)
        {
            case double d: result = d; break;
            case int i: result = i; break;
            case long l: result = l; break;
            case float f: result = f; break;
            case decimal m: result = (double)m; break;
            case short s: result = s; break;
            case byte b: result = b; break;
            case uint ui: result = ui; break;
            case ulong ul: result = ul; break;
            case ushort us: result = us; break;
            case sbyte sb: result = sb; break;
            case string text: return TryParseDouble(text, out result);
            default: result = 0; return false;
        }

        return double.IsFinite(result);
    }

    private static bool TryGetDate(object value, out DateTimeOffset result)
    {
        switch (value)
        {
            case DateTimeOffset dto: result = dto; return true;
            case DateTime dt:
                result = dt.Kind == DateTimeKind.Local ? new DateTimeOffset(dt) : new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                return true;
            case string text: return TryParseDate(text, out result);
            default: result = default; return false;
        }
    }

    private void Invalid(string reason, Action<string> warn)
    {
        Never = true;
        warn($"Condition on '{_attribute}' ({_operator}) can never match: {reason}");
    }

    private bool TryTest(object value, out bool result)
    {
        result = false;
        switch (_operator)
        {
            case ConditionOperator.Equals:
            case ConditionOperator.In:
                return TestEquality(value, out result);
            case ConditionOperator.Contains:
                if (value is not string contained)
                {
                    return false;
                }

                foreach (var s in _strings)
                {
                    if (contained.Contains(s, _comparison))
                    {
                        result = true;
                        break;
                    }
                }

                return true;
            case ConditionOperator.StartsWith:
                if (value is not string prefixed)
                {
                    return false;
                }

                foreach (var s in _strings)
                {
                    if (prefixed.StartsWith(s, _comparison))
                    {
                        result = true;
                        break;
                    }
                }

                return true;
            case ConditionOperator.EndsWith:
                if (value is not string suffixed)
                {
                    return false;
                }

                foreach (var s in _strings)
                {
                    if (suffixed.EndsWith(s, _comparison))
                    {
                        result = true;
                        break;
                    }
                }

                return true;
            case ConditionOperator.Matches:
                if (value is not string text)
                {
                    return false;
                }

                try
                {
                    result = _regex!.IsMatch(_uppercaseInput ? text.ToUpperInvariant() : text);
                    return true;
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }

            case ConditionOperator.GreaterThan:
                if (!TryGetDouble(value, out var gt))
                {
                    return false;
                }

                result = gt > _number;
                return true;
            case ConditionOperator.LessThan:
                if (!TryGetDouble(value, out var lt))
                {
                    return false;
                }

                result = lt < _number;
                return true;
            case ConditionOperator.SemVerGreaterThan:
            case ConditionOperator.SemVerLessThan:
            case ConditionOperator.SemVerEquals:
                if (value is not string versionText || !SemVer.TryParse(versionText, out var version))
                {
                    return false;
                }

                var cmp = version.CompareTo(_semVer);
                result = _operator switch
                {
                    ConditionOperator.SemVerGreaterThan => cmp > 0,
                    ConditionOperator.SemVerLessThan => cmp < 0,
                    _ => cmp == 0,
                };
                return true;
            case ConditionOperator.Before:
                if (!TryGetDate(value, out var before))
                {
                    return false;
                }

                result = before < _date;
                return true;
            case ConditionOperator.After:
                if (!TryGetDate(value, out var after))
                {
                    return false;
                }

                result = after > _date;
                return true;
            default:
                return false;
        }
    }

    private bool TestEquality(object value, out bool result)
    {
        result = false;
        switch (value)
        {
            case string s:
                foreach (var candidate in _strings)
                {
                    if (string.Equals(s, candidate, _comparison))
                    {
                        result = true;
                        break;
                    }
                }

                return true;
            case bool b:
                var text = b ? "true" : "false";
                foreach (var candidate in _strings)
                {
                    if (string.Equals(text, candidate, StringComparison.Ordinal))
                    {
                        result = true;
                        break;
                    }
                }

                return true;
            default:
                if (!TryGetDouble(value, out var number))
                {
                    return false;
                }

                foreach (var candidate in _numbers)
                {
                    if (number == candidate)
                    {
                        result = true;
                        break;
                    }
                }

                return true;
        }
    }

    internal static int SegmentDepthLimit => MaxSegmentDepth;
}

internal static class RegexCache
{
    private const int MaxEntries = 1024;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex?> Cache = new(StringComparer.Ordinal);

    /// <summary>Returns a compiled regex with a 50 ms match timeout, or <see langword="null"/> for an invalid pattern.</summary>
    public static Regex? Get(string pattern)
    {
        if (Cache.TryGetValue(pattern, out var cached))
        {
            return cached;
        }

        Regex? regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException)
        {
            regex = null;
        }

        if (Cache.Count < MaxEntries)
        {
            Cache.TryAdd(pattern, regex);
        }

        return regex;
    }
}
