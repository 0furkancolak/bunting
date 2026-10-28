using System.Text.Json;
using Bunting.Domain;
using Bunting.Evaluation;

namespace Bunting.Evaluation.Tests;

internal static class B
{
    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static Dictionary<string, JsonElement> BoolVariants() =>
        new() { ["on"] = Json("true"), ["off"] = Json("false") };

    public static Condition Cond(string attribute, ConditionOperator op, params string[] values) =>
        new(attribute, op, values);

    public static Condition Not(string attribute, ConditionOperator op, params string[] values) =>
        new(attribute, op, values, Negate: true);

    public static Rule RuleOf(string id, Serve serve, params Condition[] conditions) => new(id, null, conditions, serve);

    public static Rollout Roll(params (string Variant, int Weight)[] parts) =>
        new([.. parts.Select(p => new WeightedVariant(p.Variant, p.Weight))]);

    public static FlagSnapshot Flag(
        string key = "f",
        FlagType type = FlagType.Boolean,
        Dictionary<string, JsonElement>? variants = null,
        bool enabled = true,
        bool archived = false,
        string off = "off",
        Serve? fallthrough = null,
        params Rule[] rules) =>
        new(key, type, variants ?? BoolVariants(), enabled, archived, off, rules, fallthrough ?? Serve.Single("off"));

    public static CompiledRuleset Compile(IEnumerable<FlagSnapshot> flags, params SegmentSnapshot[] segments) =>
        CompiledRuleset.Compile(new RulesetSnapshot(1, [.. flags], segments));

    public static CompiledRuleset Compile(FlagSnapshot flag, params SegmentSnapshot[] segments) => Compile([flag], segments);

    public static SegmentSnapshot Segment(
        string key, string[]? included = null, string[]? excluded = null, params Condition[] conditions) =>
        new(key, included ?? [], excluded ?? [], conditions);

    public static EvaluationContext Ctx(string? key = "user-1", params (string Name, object? Value)[] attrs) =>
        new(key, attrs.ToDictionary(a => a.Name, a => a.Value));

    /// <summary>Evaluates a single rule "attr op values => on" against a context; true when the rule matched.</summary>
    public static bool Matches(Condition condition, EvaluationContext context, params SegmentSnapshot[] segments)
    {
        var flag = Flag(rules: RuleOf("r", Serve.Single("on"), condition));
        var result = Compile(flag, segments).Evaluate("f", context);
        return result.Reason == EvaluationReason.TargetingMatch;
    }
}
