using Bunting.Domain;
using static Bunting.Evaluation.Tests.B;
using Op = Bunting.Domain.ConditionOperator;

namespace Bunting.Evaluation.Tests;

public class CompilationTests
{
    private static void AssertParseError(CompiledRuleset ruleset, string flagKey = "f")
    {
        var r = ruleset.Evaluate(flagKey, Ctx());
        Assert.Equal(EvaluationReason.Error, r.Reason);
        Assert.Equal(EvaluationErrorCode.ParseError, r.ErrorCode);
        Assert.Contains(ruleset.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.FlagKey == flagKey);
    }

    [Fact]
    public void Rollout_weights_must_sum_to_10000() =>
        AssertParseError(Compile(Flag(fallthrough: Serve.Split(Roll(("on", 5_000), ("off", 4_000))))));

    [Fact]
    public void Negative_weights_are_rejected() =>
        AssertParseError(Compile(Flag(fallthrough: Serve.Split(Roll(("on", 11_000), ("off", -1_000))))));

    [Fact]
    public void Unknown_variant_references_are_rejected()
    {
        AssertParseError(Compile(Flag(off: "missing")));
        AssertParseError(Compile(Flag(fallthrough: Serve.Single("missing"))));
        AssertParseError(Compile(Flag(rules: RuleOf("r", Serve.Single("missing")))));
        AssertParseError(Compile(Flag(fallthrough: Serve.Split(Roll(("on", 5_000), ("ghost", 5_000))))));
    }

    [Fact]
    public void Serve_must_name_exactly_one_target()
    {
        AssertParseError(Compile(Flag(fallthrough: new Serve(null, null))));
        AssertParseError(Compile(Flag(fallthrough: new Serve("on", Roll(("on", 10_000))))));
    }

    [Fact]
    public void Variant_values_must_match_the_flag_type() =>
        AssertParseError(Compile(Flag(type: FlagType.String)));

    [Fact]
    public void Integer_flags_reject_fractions()
    {
        var variants = new Dictionary<string, System.Text.Json.JsonElement> { ["a"] = Json("1.5"), ["off"] = Json("0") };
        AssertParseError(Compile(Flag(type: FlagType.Integer, variants: variants)));
    }

    [Fact]
    public void Flag_without_variants_is_rejected() =>
        AssertParseError(Compile(Flag(variants: [])));

    [Fact]
    public void One_broken_flag_does_not_affect_others()
    {
        var ruleset = Compile([Flag("broken", off: "missing"), Flag("fine", fallthrough: Serve.Single("on"))]);
        Assert.Equal(EvaluationErrorCode.ParseError, ruleset.Evaluate("broken", Ctx()).ErrorCode);
        Assert.Equal("on", ruleset.Evaluate("fine", Ctx()).Variant);
        Assert.Equal(2, ruleset.FlagCount);
    }

    [Fact]
    public void Duplicate_flag_keys_keep_the_first_definition()
    {
        var ruleset = Compile([Flag("f", fallthrough: Serve.Single("on")), Flag("f", fallthrough: Serve.Single("off"))]);
        Assert.Equal("on", ruleset.Evaluate("f", Ctx()).Variant);
        Assert.Contains(ruleset.Diagnostics, d => d.Message.Contains("Duplicate"));
    }

    [Fact]
    public void Valid_ruleset_has_no_diagnostics_and_exposes_version()
    {
        var ruleset = CompiledRuleset.Compile(new RulesetSnapshot(42, [Flag()], []));
        Assert.Empty(ruleset.Diagnostics);
        Assert.Equal(42, ruleset.Version);
        Assert.True(ruleset.ContainsFlag("f"));
    }

    [Fact]
    public void Empty_ruleset_reports_flags_as_not_found() =>
        Assert.Equal(EvaluationErrorCode.FlagNotFound, CompiledRuleset.Empty.Evaluate("x", Ctx()).ErrorCode);

    [Fact]
    public void Snapshot_round_trips_through_json()
    {
        var snapshot = new RulesetSnapshot(
            3,
            [Flag(rules: RuleOf("r", Serve.Split(Roll(("on", 2_500), ("off", 7_500))), Cond("plan", Op.In, "pro", "team"), Not("v", Op.SemVerLessThan, "2.0.0")))],
            [Segment("beta", included: ["a"], conditions: Cond("country", Op.Equals, "TR"))]);

        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        Assert.Contains("\"SemVerLessThan\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<RulesetSnapshot>(json)!;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(snapshot), System.Text.Json.JsonSerializer.Serialize(back));

        var ruleset = CompiledRuleset.Compile(back);
        Assert.Empty(ruleset.Diagnostics);
        Assert.Equal("r", ruleset.Evaluate("f", Ctx("user-1", ("plan", "pro"), ("v", "3.0.0"))).RuleId);
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("0", true)]
    [InlineData("a_b.c-d", true)]
    [InlineData("a/b", false)]
    [InlineData("a.b-c.d", true)]
    [InlineData("", false)]
    [InlineData("-a", false)]
    [InlineData("A", false)]
    [InlineData("has space", false)]
    public void FlagKey_validation(string key, bool valid) => Assert.Equal(valid, FlagKey.IsValid(key));

    [Fact]
    public void FlagKey_length_limit_is_100()
    {
        Assert.True(FlagKey.IsValid(new string('a', 100)));
        Assert.False(FlagKey.IsValid(new string('a', 101)));
        Assert.False(FlagKey.IsValid(null));
    }
}

public class FlagInfoTests
{
    [Fact]
    public void Flags_lists_metadata_ordered_by_key_including_invalid_ones()
    {
        var ruleset = B.Compile(
        [
            B.Flag("b") with { ClientSideAvailable = true },
            B.Flag("a", archived: true),
            B.Flag("broken", off: "missing"),
        ]);

        Assert.Equal(["a", "b", "broken"], ruleset.Flags.Select(f => f.Key));
        Assert.True(ruleset.Flags[0].Archived);
        Assert.True(ruleset.Flags[1].ClientSideAvailable);
        Assert.False(ruleset.Flags[2].IsValid);
    }
}
