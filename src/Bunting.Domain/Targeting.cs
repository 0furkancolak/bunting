namespace Bunting.Domain;

/// <summary>A single targeting predicate. All conditions of a rule must match (AND).</summary>
/// <param name="Attribute">Context attribute name; <c>targetingKey</c> addresses the targeting key. Ignored by <see cref="ConditionOperator.InSegment"/>.</param>
/// <param name="Values">Operands. Segment keys for <see cref="ConditionOperator.InSegment"/>.</param>
/// <param name="Negate">Inverts the result when the condition is evaluable (see evaluation-semantics.md).</param>
public sealed record Condition(
    string Attribute,
    ConditionOperator Operator,
    IReadOnlyList<string> Values,
    bool Negate = false);

/// <summary>One slice of a percentage rollout.</summary>
/// <param name="Weight">Share in basis points; all weights of a rollout sum to <see cref="Rollout.TotalWeight"/>.</param>
public sealed record WeightedVariant(string Variant, int Weight);

/// <summary>Deterministic percentage split. Order matters for stickiness (see evaluation-semantics.md).</summary>
/// <param name="BucketBy">Attribute whose value is hashed; defaults to <c>targetingKey</c>.</param>
/// <param name="Salt">Optional salt to decorrelate rollouts of the same flag.</param>
public sealed record Rollout(
    IReadOnlyList<WeightedVariant> Variants,
    string? BucketBy = null,
    string? Salt = null)
{
    public const int TotalWeight = 10_000;
    public const string DefaultBucketBy = "targetingKey";
}

/// <summary>What to serve: exactly one of a fixed variant or a rollout.</summary>
public sealed record Serve(string? Variant, Rollout? Rollout)
{
    public static Serve Single(string variant) => new(variant, null);

    public static Serve Split(Rollout rollout) => new(null, rollout);
}

public sealed record Rule(string Id, string? Description, IReadOnlyList<Condition> Conditions, Serve Serve);
