using System.Text.Json;
using Bunting.Domain;
using static Bunting.Evaluation.Tests.B;
using Op = Bunting.Domain.ConditionOperator;

namespace Bunting.Evaluation.Tests;

public class EvaluationFlowTests
{
    [Fact]
    public void Unknown_flag_is_FLAG_NOT_FOUND()
    {
        var r = Compile(Flag()).Evaluate("nope", Ctx());
        Assert.Equal(EvaluationReason.Error, r.Reason);
        Assert.Equal(EvaluationErrorCode.FlagNotFound, r.ErrorCode);
        Assert.Equal(JsonValueKind.Undefined, r.Value.ValueKind);
        Assert.Null(r.Variant);
    }

    [Fact]
    public void Disabled_flag_serves_off_variant_with_DISABLED()
    {
        var r = Compile(Flag(enabled: false, fallthrough: Serve.Single("on"))).Evaluate("f", Ctx());
        Assert.Equal(EvaluationReason.Disabled, r.Reason);
        Assert.Equal("off", r.Variant);
        Assert.False(r.Value.GetBoolean());
        Assert.False(r.IsError);
    }

    [Fact]
    public void Archived_flag_behaves_like_disabled()
    {
        var r = Compile(Flag(archived: true, fallthrough: Serve.Single("on"))).Evaluate("f", Ctx());
        Assert.Equal(EvaluationReason.Disabled, r.Reason);
        Assert.Equal("off", r.Variant);
    }

    [Fact]
    public void Disabled_flag_ignores_matching_rules()
    {
        var flag = Flag(enabled: false, rules: RuleOf("r", Serve.Single("on"), Cond("x", Op.Equals, "1")));
        Assert.Equal(EvaluationReason.Disabled, Compile(flag).Evaluate("f", Ctx("u", ("x", "1"))).Reason);
    }

    [Fact]
    public void First_matching_rule_wins_and_reports_rule_id()
    {
        var flag = Flag(
            fallthrough: Serve.Single("off"),
            rules:
            [
                RuleOf("no", Serve.Single("off"), Cond("plan", Op.Equals, "free")),
                RuleOf("first", Serve.Single("on"), Cond("plan", Op.Equals, "pro")),
                RuleOf("second", Serve.Single("off"), Cond("plan", Op.Equals, "pro")),
            ]);
        var r = Compile(flag).Evaluate("f", Ctx("u", ("plan", "pro")));
        Assert.Equal(EvaluationReason.TargetingMatch, r.Reason);
        Assert.Equal("first", r.RuleId);
        Assert.Equal("on", r.Variant);
        Assert.True(r.Value.GetBoolean());
    }

    [Fact]
    public void All_conditions_of_a_rule_must_match()
    {
        var flag = Flag(rules: RuleOf("r", Serve.Single("on"), Cond("a", Op.Equals, "1"), Cond("b", Op.Equals, "2")));
        var ruleset = Compile(flag);
        Assert.Equal(EvaluationReason.TargetingMatch, ruleset.Evaluate("f", Ctx("u", ("a", "1"), ("b", "2"))).Reason);
        Assert.Equal(EvaluationReason.Default, ruleset.Evaluate("f", Ctx("u", ("a", "1"), ("b", "x"))).Reason);
    }

    [Fact]
    public void Rule_without_conditions_matches_everyone()
    {
        var flag = Flag(rules: RuleOf("all", Serve.Single("on")));
        Assert.Equal("all", Compile(flag).Evaluate("f", Ctx(null)).RuleId);
    }

    [Fact]
    public void Fallthrough_single_variant_is_DEFAULT()
    {
        var r = Compile(Flag(fallthrough: Serve.Single("on"))).Evaluate("f", Ctx());
        Assert.Equal(EvaluationReason.Default, r.Reason);
        Assert.Equal("on", r.Variant);
        Assert.Null(r.RuleId);
    }

    [Fact]
    public void Rule_with_rollout_is_SPLIT_and_carries_rule_id()
    {
        var flag = Flag(rules: RuleOf("canary", Serve.Split(Roll(("on", 10_000))), Cond("x", Op.Equals, "1")));
        var r = Compile(flag).Evaluate("f", Ctx("u", ("x", "1")));
        Assert.Equal(EvaluationReason.Split, r.Reason);
        Assert.Equal("canary", r.RuleId);
        Assert.Equal("on", r.Variant);
    }

    [Fact]
    public void Fallthrough_rollout_is_SPLIT()
    {
        var r = Compile(Flag(fallthrough: Serve.Split(Roll(("on", 10_000))))).Evaluate("f", Ctx("u"));
        Assert.Equal(EvaluationReason.Split, r.Reason);
        Assert.Null(r.RuleId);
    }

    [Fact]
    public void Rule_rollout_without_bucketing_value_skips_the_rule()
    {
        var flag = Flag(
            fallthrough: Serve.Single("off"),
            rules: RuleOf("r", Serve.Split(Roll(("on", 10_000))), Cond("x", Op.Equals, "1")));
        var r = Compile(flag).Evaluate("f", Ctx(null, ("x", "1")));
        Assert.Equal(EvaluationReason.Default, r.Reason);
        Assert.Equal("off", r.Variant);
        Assert.False(r.IsError);
    }

    [Fact]
    public void Skipped_rollout_rule_lets_following_rules_match()
    {
        var flag = Flag(rules:
        [
            RuleOf("needs-key", Serve.Split(Roll(("on", 10_000)))),
            RuleOf("static", Serve.Single("on"), Cond("x", Op.Equals, "1")),
        ]);
        Assert.Equal("static", Compile(flag).Evaluate("f", Ctx(null, ("x", "1"))).RuleId);
    }

    [Fact]
    public void Fallthrough_rollout_without_targeting_key_returns_off_variant_and_TARGETING_KEY_MISSING()
    {
        var r = Compile(Flag(fallthrough: Serve.Split(Roll(("on", 10_000))))).Evaluate("f", Ctx(null));
        Assert.Equal(EvaluationReason.Error, r.Reason);
        Assert.Equal(EvaluationErrorCode.TargetingKeyMissing, r.ErrorCode);
        Assert.Equal("off", r.Variant);
        Assert.False(r.Value.GetBoolean());
    }

    [Fact]
    public void BucketBy_attribute_is_used_and_missing_value_counts_as_missing_key()
    {
        var rollout = Roll(("on", 10_000)) with { BucketBy = "orgId" };
        var ruleset = Compile(Flag(fallthrough: Serve.Split(rollout)));
        Assert.Equal(EvaluationReason.Split, ruleset.Evaluate("f", Ctx(null, ("orgId", "acme"))).Reason);
        Assert.Equal(EvaluationErrorCode.TargetingKeyMissing, ruleset.Evaluate("f", Ctx("u")).ErrorCode);
    }

    [Theory]
    [InlineData(FlagType.String)]
    [InlineData(FlagType.Integer)]
    [InlineData(FlagType.Double)]
    [InlineData(FlagType.Object)]
    public void Requesting_the_wrong_type_is_TYPE_MISMATCH(FlagType requested)
    {
        var r = Compile(Flag()).Evaluate("f", Ctx(), requested);
        Assert.Equal(EvaluationReason.Error, r.Reason);
        Assert.Equal(EvaluationErrorCode.TypeMismatch, r.ErrorCode);
    }

    [Fact]
    public void Type_mismatch_applies_to_disabled_flags_too()
    {
        var r = Compile(Flag(enabled: false)).Evaluate("f", Ctx(), FlagType.String);
        Assert.Equal(EvaluationErrorCode.TypeMismatch, r.ErrorCode);
    }

    [Fact]
    public void Matching_type_is_accepted()
    {
        var r = Compile(Flag(fallthrough: Serve.Single("on"))).Evaluate("f", Ctx(), FlagType.Boolean);
        Assert.False(r.IsError);
    }

    [Fact]
    public void Multivariate_string_and_json_values_are_returned()
    {
        var strings = Flag(
            "color",
            FlagType.String,
            new() { ["red"] = Json("\"#f00\""), ["blue"] = Json("\"#00f\"") },
            off: "red",
            fallthrough: Serve.Single("blue"));
        var objects = Flag(
            "cfg",
            FlagType.Object,
            new() { ["a"] = Json("{\"limit\":10}"), ["b"] = Json("[1,2]") },
            off: "a",
            fallthrough: Serve.Single("a"));
        var numbers = Flag(
            "n",
            FlagType.Integer,
            new() { ["x"] = Json("7"), ["y"] = Json("9") },
            off: "x",
            fallthrough: Serve.Single("y"));
        var ruleset = Compile([strings, objects, numbers]);
        Assert.Equal("#00f", ruleset.Evaluate("color", Ctx(), FlagType.String).Value.GetString());
        Assert.Equal(10, ruleset.Evaluate("cfg", Ctx(), FlagType.Object).Value.GetProperty("limit").GetInt32());
        Assert.Equal(9, ruleset.Evaluate("n", Ctx(), FlagType.Integer).Value.GetInt64());
    }

    [Fact]
    public void Evaluation_is_deterministic()
    {
        var flag = Flag(
            fallthrough: Serve.Split(Roll(("on", 5_000), ("off", 5_000))),
            rules: RuleOf("r", Serve.Split(Roll(("on", 3_000), ("off", 7_000))), Cond("plan", Op.Equals, "pro")));
        var a = Compile(flag);
        var b = Compile(flag);
        for (var i = 0; i < 500; i++)
        {
            var ctx = Ctx($"user-{i}", ("plan", i % 2 == 0 ? "pro" : "free"));
            var first = a.Evaluate("f", ctx);
            Assert.Equal(first.Variant, a.Evaluate("f", ctx).Variant);
            Assert.Equal(first.Variant, b.Evaluate("f", ctx).Variant);
            Assert.Equal(first.RuleId, b.Evaluate("f", ctx).RuleId);
        }
    }

    [Fact]
    public void Rule_conditions_may_use_segments_and_rollouts_together()
    {
        var flag = Flag(rules: RuleOf("beta", Serve.Split(Roll(("on", 10_000))), Cond("-", Op.InSegment, "beta")));
        var ruleset = Compile([flag], Segment("beta", included: ["alice"]));
        Assert.Equal("beta", ruleset.Evaluate("f", Ctx("alice")).RuleId);
        Assert.Null(ruleset.Evaluate("f", Ctx("bob")).RuleId);
    }
}
