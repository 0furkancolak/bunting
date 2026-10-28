using Bunting.Domain;
using static Bunting.Evaluation.Tests.B;
using Op = Bunting.Domain.ConditionOperator;

namespace Bunting.Evaluation.Tests;

public class OperatorTests
{
    [Theory]
    [InlineData("pro", true)]
    [InlineData("Pro", false)] // case-sensitive
    [InlineData("free", false)]
    public void Equals_is_ordinal_and_case_sensitive(string plan, bool expected) =>
        Assert.Equal(expected, Matches(Cond("plan", Op.Equals, "pro"), Ctx("u", ("plan", plan))));

    [Theory]
    [InlineData("a", true)]
    [InlineData("c", true)]
    [InlineData("d", false)]
    public void In_matches_any_value(string value, bool expected) =>
        Assert.Equal(expected, Matches(Cond("x", Op.In, "a", "b", "c"), Ctx("u", ("x", value))));

    [Fact]
    public void Equals_compares_numbers_numerically()
    {
        Assert.True(Matches(Cond("n", Op.Equals, "5"), Ctx("u", ("n", 5))));
        Assert.True(Matches(Cond("n", Op.Equals, "5.0"), Ctx("u", ("n", 5L))));
        Assert.False(Matches(Cond("n", Op.Equals, "5"), Ctx("u", ("n", 6.5))));
    }

    [Fact]
    public void Equals_compares_booleans_to_lowercase_literals()
    {
        Assert.True(Matches(Cond("beta", Op.Equals, "true"), Ctx("u", ("beta", true))));
        Assert.False(Matches(Cond("beta", Op.Equals, "true"), Ctx("u", ("beta", false))));
        Assert.False(Matches(Cond("beta", Op.Equals, "True"), Ctx("u", ("beta", true))));
    }

    [Theory]
    [InlineData("hello world", "lo w", true)]
    [InlineData("hello world", "LO", false)]
    [InlineData("hello", "xyz", false)]
    public void Contains(string value, string needle, bool expected) =>
        Assert.Equal(expected, Matches(Cond("s", Op.Contains, needle), Ctx("u", ("s", value))));

    [Theory]
    [InlineData("user@corp.com", "user@", true)]
    [InlineData("user@corp.com", "USER", false)]
    [InlineData("user@corp.com", "corp", false)]
    public void StartsWith(string value, string prefix, bool expected) =>
        Assert.Equal(expected, Matches(Cond("s", Op.StartsWith, prefix), Ctx("u", ("s", value))));

    [Theory]
    [InlineData("user@corp.com", "@corp.com", true)]
    [InlineData("user@corp.com", "@CORP.com", false)]
    [InlineData("user@corp.com", "user", false)]
    public void EndsWith(string value, string suffix, bool expected) =>
        Assert.Equal(expected, Matches(Cond("s", Op.EndsWith, suffix), Ctx("u", ("s", value))));

    [Fact]
    public void String_operators_do_not_apply_to_non_strings()
    {
        Assert.False(Matches(Cond("n", Op.Contains, "1"), Ctx("u", ("n", 123))));
        Assert.False(Matches(Cond("n", Op.Contains, "1") with { Negate = true }, Ctx("u", ("n", 123))));
    }

    [Theory]
    [InlineData("abc123", @"^[a-z]+\d+$", true)]
    [InlineData("ABC123", @"^[a-z]+\d+$", false)]
    [InlineData("x", @"^\d+$", false)]
    public void Matches_uses_regex(string value, string pattern, bool expected) =>
        Assert.Equal(expected, Matches(Cond("s", Op.Matches, pattern), Ctx("u", ("s", value))));

    [Fact]
    public void Matches_with_catastrophic_pattern_times_out_and_does_not_match()
    {
        var input = new string('a', 60) + "!";
        var condition = Cond("s", Op.Matches, "^(a+)+$");
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(Matches(condition, Ctx("u", ("s", input))));
        Assert.False(Matches(condition with { Negate = true }, Ctx("u", ("s", input))));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"took {started.Elapsed}");
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(5, false)]
    [InlineData(4, false)]
    public void GreaterThan(double value, bool expected) =>
        Assert.Equal(expected, Matches(Cond("age", Op.GreaterThan, "5"), Ctx("u", ("age", value))));

    [Theory]
    [InlineData(3, true)]
    [InlineData(5, false)]
    [InlineData(7, false)]
    public void LessThan(int value, bool expected) =>
        Assert.Equal(expected, Matches(Cond("age", Op.LessThan, "5"), Ctx("u", ("age", value))));

    [Fact]
    public void Numeric_operators_accept_numeric_strings_and_reject_others()
    {
        Assert.True(Matches(Cond("n", Op.GreaterThan, "5"), Ctx("u", ("n", "10"))));
        Assert.False(Matches(Cond("n", Op.GreaterThan, "5"), Ctx("u", ("n", "abc"))));
        Assert.False(Matches(Cond("n", Op.GreaterThan, "5") with { Negate = true }, Ctx("u", ("n", "abc"))));
        Assert.False(Matches(Cond("n", Op.GreaterThan, "5"), Ctx("u", ("n", double.NaN))));
    }

    [Theory]
    [InlineData("2.0.0", Op.SemVerGreaterThan, "1.9.9", true)]
    [InlineData("1.10.0", Op.SemVerGreaterThan, "1.9.0", true)]
    [InlineData("1.0.0-rc.1", Op.SemVerGreaterThan, "1.0.0", false)]
    [InlineData("1.0.0", Op.SemVerGreaterThan, "1.0.0-rc.1", true)]
    [InlineData("1.2.3", Op.SemVerLessThan, "1.2.4", true)]
    [InlineData("1.2.3", Op.SemVerLessThan, "1.2.3", false)]
    [InlineData("1.2.3", Op.SemVerEquals, "1.2.3+meta", true)]
    [InlineData("1.2", Op.SemVerEquals, "1.2.0", true)]
    [InlineData("1.2.4", Op.SemVerEquals, "1.2.3", false)]
    [InlineData("not-a-version", Op.SemVerEquals, "1.2.3", false)]
    public void SemVer_operators(string value, Op op, string operand, bool expected) =>
        Assert.Equal(expected, Matches(Cond("appVersion", op, operand), Ctx("u", ("appVersion", value))));

    [Fact]
    public void SemVer_negate_only_applies_to_parsable_versions()
    {
        var notEq = Not("v", Op.SemVerEquals, "1.0.0");
        Assert.True(Matches(notEq, Ctx("u", ("v", "2.0.0"))));
        Assert.False(Matches(notEq, Ctx("u", ("v", "garbage"))));
    }

    [Theory]
    [InlineData("2024-01-01T00:00:00Z", Op.Before, "2024-06-01T00:00:00Z", true)]
    [InlineData("2024-07-01T00:00:00Z", Op.Before, "2024-06-01T00:00:00Z", false)]
    [InlineData("2024-07-01T00:00:00Z", Op.After, "2024-06-01T00:00:00Z", true)]
    [InlineData("2024-06-01T00:00:00Z", Op.After, "2024-06-01T00:00:00Z", false)]
    [InlineData("2024-06-01T02:00:00+02:00", Op.After, "2024-05-31T23:59:59Z", true)] // same instant + 1s
    [InlineData("2024-06-01", Op.Before, "2024-06-02", true)]
    [InlineData("nonsense", Op.Before, "2024-06-02", false)]
    public void Date_operators(string value, Op op, string operand, bool expected) =>
        Assert.Equal(expected, Matches(Cond("signup", op, operand), Ctx("u", ("signup", value))));

    [Fact]
    public void Date_operators_accept_native_date_types()
    {
        var cond = Cond("d", Op.After, "2024-01-01T00:00:00Z");
        Assert.True(Matches(cond, Ctx("u", ("d", new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)))));
        Assert.True(Matches(cond, Ctx("u", ("d", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)))));
        Assert.False(Matches(cond, Ctx("u", ("d", 42))));
    }

    [Fact]
    public void Missing_attribute_never_matches_even_when_negated()
    {
        foreach (var op in new[] { Op.Equals, Op.In, Op.Contains, Op.StartsWith, Op.EndsWith, Op.Matches, Op.GreaterThan, Op.LessThan, Op.SemVerEquals, Op.Before })
        {
            Assert.False(Matches(Cond("missing", op, "1"), Ctx("u", ("other", "x"))), $"{op}");
            Assert.False(Matches(Not("missing", op, "1"), Ctx("u", ("other", "x"))), $"{op} negated");
        }
    }

    [Fact]
    public void Null_attribute_value_counts_as_missing() =>
        Assert.False(Matches(Not("x", Op.Equals, "a"), Ctx("u", ("x", null))));

    [Fact]
    public void Negate_inverts_present_attributes()
    {
        Assert.True(Matches(Not("plan", Op.Equals, "pro"), Ctx("u", ("plan", "free"))));
        Assert.False(Matches(Not("plan", Op.Equals, "pro"), Ctx("u", ("plan", "pro"))));
    }

    [Fact]
    public void TargetingKey_can_be_used_as_an_attribute()
    {
        Assert.True(Matches(Cond("targetingKey", Op.StartsWith, "internal-"), Ctx("internal-42")));
        Assert.False(Matches(Cond("targetingKey", Op.StartsWith, "internal-"), Ctx(null)));
        Assert.False(Matches(Not("targetingKey", Op.StartsWith, "internal-"), Ctx(null)));
    }

    [Theory]
    [InlineData("tr", "TR", true)]
    [InlineData("TR", "tr", true)]
    [InlineData("de", "TR", false)]
    public void Country_is_normalized_to_upper_case(string contextValue, string conditionValue, bool expected)
    {
        Assert.Equal(expected, Matches(Cond("country", Op.Equals, conditionValue), Ctx("u", ("country", contextValue))));
        Assert.Equal(expected, Matches(Cond("country", Op.In, "XX", conditionValue), Ctx("u", ("country", contextValue))));
    }

    [Fact]
    public void Country_normalization_applies_to_regex_and_other_attributes_stay_case_sensitive()
    {
        Assert.True(Matches(Cond("country", Op.Matches, "^(TR|DE)$"), Ctx("u", ("country", "tr"))));
        Assert.False(Matches(Cond("region", Op.Equals, "EU"), Ctx("u", ("region", "eu"))));
    }

    [Fact]
    public void Attribute_names_are_case_sensitive() =>
        Assert.False(Matches(Cond("Plan", Op.Equals, "pro"), Ctx("u", ("plan", "pro"))));

    [Theory]
    [InlineData(Op.GreaterThan, "abc")]
    [InlineData(Op.SemVerEquals, "1.x")]
    [InlineData(Op.Before, "yesterday")]
    [InlineData(Op.Matches, "([")]
    [InlineData(Op.Equals, null)]
    public void Invalid_operands_never_match_and_are_reported(Op op, string? operand)
    {
        var condition = new Condition("x", op, operand is null ? [] : [operand], Negate: true);
        var flag = Flag(rules: RuleOf("r", Serve.Single("on"), condition));
        var ruleset = Compile(flag);
        Assert.Contains(ruleset.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning && d.FlagKey == "f");
        Assert.NotEqual(EvaluationReason.TargetingMatch, ruleset.Evaluate("f", Ctx("u", ("x", "anything"))).Reason);
    }
}
