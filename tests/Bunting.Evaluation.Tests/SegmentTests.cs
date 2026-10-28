using Bunting.Domain;
using static Bunting.Evaluation.Tests.B;
using Op = Bunting.Domain.ConditionOperator;

namespace Bunting.Evaluation.Tests;

public class SegmentTests
{
    private static readonly Condition InBeta = Cond("-", Op.InSegment, "beta");

    [Fact]
    public void Included_keys_are_members() =>
        Assert.True(Matches(InBeta, Ctx("alice"), Segment("beta", included: ["alice"])));

    [Fact]
    public void Unlisted_keys_are_not_members() =>
        Assert.False(Matches(InBeta, Ctx("bob"), Segment("beta", included: ["alice"])));

    [Fact]
    public void Excluded_wins_over_included_and_conditions() =>
        Assert.False(Matches(
            InBeta,
            Ctx("alice", ("plan", "pro")),
            Segment("beta", ["alice"], ["alice"], Cond("plan", Op.Equals, "pro"))));

    [Fact]
    public void Conditions_define_membership_with_AND()
    {
        var seg = Segment("beta", conditions: [Cond("plan", Op.Equals, "pro"), Cond("country", Op.Equals, "TR")]);
        Assert.True(Matches(InBeta, Ctx("u", ("plan", "pro"), ("country", "TR")), seg));
        Assert.False(Matches(InBeta, Ctx("u", ("plan", "pro"), ("country", "DE")), seg));
    }

    [Fact]
    public void Segment_without_members_or_conditions_is_empty() =>
        Assert.False(Matches(InBeta, Ctx("u"), Segment("beta")));

    [Fact]
    public void Negated_InSegment_works_without_targeting_key()
    {
        var notBeta = Not("-", Op.InSegment, "beta");
        Assert.True(Matches(notBeta, Ctx(null), Segment("beta", included: ["alice"])));
        Assert.False(Matches(notBeta, Ctx("alice"), Segment("beta", included: ["alice"])));
    }

    [Fact]
    public void Multiple_segment_keys_match_any() =>
        Assert.True(Matches(
            Cond("-", Op.InSegment, "a", "b"), Ctx("u"), Segment("a", included: ["x"]), Segment("b", included: ["u"])));

    [Fact]
    public void Segments_can_reference_other_segments() =>
        Assert.True(Matches(
            InBeta,
            Ctx("alice"),
            Segment("beta", conditions: Cond("-", Op.InSegment, "staff")),
            Segment("staff", included: ["alice"])));

    [Fact]
    public void Unknown_segment_never_matches_and_is_reported()
    {
        var flag = Flag(rules: RuleOf("r", Serve.Single("on"), Cond("-", Op.InSegment, "ghost")));
        var ruleset = Compile(flag);
        Assert.NotEqual(EvaluationReason.TargetingMatch, ruleset.Evaluate("f", Ctx("u")).Reason);
        Assert.Contains(ruleset.Diagnostics, d => d.Message.Contains("ghost"));
    }

    [Fact]
    public void Segment_cycles_terminate_and_are_reported()
    {
        var flag = Flag(rules: RuleOf("r", Serve.Single("on"), Cond("-", Op.InSegment, "a")));
        var ruleset = Compile(
            [flag],
            Segment("a", conditions: Cond("-", Op.InSegment, "b")),
            Segment("b", conditions: Cond("-", Op.InSegment, "a")));
        Assert.NotEqual(EvaluationReason.TargetingMatch, ruleset.Evaluate("f", Ctx("u")).Reason);
        Assert.Contains(ruleset.Diagnostics, d => d.Message.Contains("cycle"));
    }
}
