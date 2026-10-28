namespace Bunting.Evaluation;

/// <summary>
/// Immutable input of an evaluation: an optional targeting key plus arbitrary attributes.
/// Supported attribute value types: string, bool, numeric primitives, <see cref="DateTimeOffset"/>, <see cref="DateTime"/>.
/// </summary>
public sealed class EvaluationContext
{
    public const string TargetingKeyAttribute = "targetingKey";

    private static readonly IReadOnlyDictionary<string, object?> NoAttributes = new Dictionary<string, object?>(0);

    public static EvaluationContext Empty { get; } = new(null);

    public EvaluationContext(string? targetingKey, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        TargetingKey = string.IsNullOrEmpty(targetingKey) ? null : targetingKey;
        Attributes = attributes ?? NoAttributes;
    }

    /// <summary>The targeting key, or <see langword="null"/> when absent or empty.</summary>
    public string? TargetingKey { get; }

    public IReadOnlyDictionary<string, object?> Attributes { get; }

    internal object? GetAttribute(string name) =>
        Attributes.TryGetValue(name, out var value) ? value : null;
}
