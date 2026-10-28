using Bunting.Domain;

namespace Bunting.Evaluation;

/// <summary>
/// An immutable, pre-compiled ruleset. Evaluation is a pure, thread-safe function: no I/O, no locks,
/// no JSON parsing; the same ruleset and context always yield the same result.
/// </summary>
public sealed class CompiledRuleset
{
    private readonly Dictionary<string, CompiledFlag> _flags;
    private readonly HashSet<string> _invalidFlags;

    internal CompiledRuleset(
        long version,
        Dictionary<string, CompiledFlag> flags,
        HashSet<string> invalidFlags,
        List<RulesetDiagnostic> diagnostics)
    {
        Version = version;
        _flags = flags;
        _invalidFlags = invalidFlags;
        Diagnostics = diagnostics;
    }

    public long Version { get; }

    /// <summary>Problems found while compiling. Errors make a flag evaluate to <c>PARSE_ERROR</c>.</summary>
    public IReadOnlyList<RulesetDiagnostic> Diagnostics { get; }

    public int FlagCount => _flags.Count + _invalidFlags.Count;

    public static CompiledRuleset Empty { get; } = Compile(new RulesetSnapshot(0, [], []));

    /// <summary>Compiles a snapshot. Never throws for malformed content; problems are reported via <see cref="Diagnostics"/>.</summary>
    public static CompiledRuleset Compile(RulesetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return RulesetCompiler.Compile(snapshot);
    }

    public bool ContainsFlag(string flagKey) => _flags.ContainsKey(flagKey) || _invalidFlags.Contains(flagKey);

    /// <summary>
    /// Evaluates a flag. <paramref name="expectedType"/> is the type requested by the caller; a mismatch yields
    /// <c>TYPE_MISMATCH</c>. Pass <see langword="null"/> to skip the check (e.g. OFREP bulk evaluation).
    /// </summary>
    public EvaluationResult Evaluate(string flagKey, EvaluationContext context, FlagType? expectedType = null)
    {
        if (!_flags.TryGetValue(flagKey, out var flag))
        {
            return EvaluationResult.Error(
                flagKey,
                _invalidFlags.Contains(flagKey) ? EvaluationErrorCode.ParseError : EvaluationErrorCode.FlagNotFound);
        }

        // Step 5 of the semantics: applies to every outcome that carries a value, so it is checked up-front.
        if (expectedType is { } expected && expected != flag.Type)
        {
            return EvaluationResult.Error(flagKey, EvaluationErrorCode.TypeMismatch);
        }

        if (!flag.Enabled)
        {
            return Result(flag, flag.OffVariant, EvaluationReason.Disabled);
        }

        foreach (var rule in flag.Rules)
        {
            if (!rule.Matches(context))
            {
                continue;
            }

            var index = rule.Serve.Pick(context);
            if (index < 0)
            {
                continue; // rollout cannot bucket this context: the rule does not match
            }

            return Result(
                flag,
                index,
                rule.Serve.IsSplit ? EvaluationReason.Split : EvaluationReason.TargetingMatch,
                ruleId: rule.Id);
        }

        var picked = flag.Fallthrough.Pick(context);
        if (picked < 0)
        {
            var off = flag.Variants[flag.OffVariant];
            return new EvaluationResult(
                flag.Key, off.Value, off.Name, EvaluationReason.Error, EvaluationErrorCode.TargetingKeyMissing);
        }

        return Result(flag, picked, flag.Fallthrough.IsSplit ? EvaluationReason.Split : EvaluationReason.Default);
    }

    private static EvaluationResult Result(CompiledFlag flag, int variantIndex, EvaluationReason reason, string? ruleId = null)
    {
        var variant = flag.Variants[variantIndex];
        return new EvaluationResult(flag.Key, variant.Value, variant.Name, reason, EvaluationErrorCode.None, ruleId);
    }
}
