using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bunting.Domain;

namespace Bunting.Evaluation;

internal readonly record struct CompiledVariant(string Name, JsonElement Value);

internal sealed class CompiledRollout
{
    private readonly byte[] _prefix;
    private readonly int[] _cumulative;
    private readonly int[] _variantIndex;
    private readonly string _bucketBy;
    private readonly bool _bucketByTargetingKey;

    public CompiledRollout(string flagKey, Rollout source, Func<string, int> indexOf)
    {
        var salt = source.Salt ?? string.Empty;
        _prefix = Encoding.UTF8.GetBytes($"{flagKey}.{salt}.");
        _bucketBy = string.IsNullOrEmpty(source.BucketBy) ? Rollout.DefaultBucketBy : source.BucketBy;
        _bucketByTargetingKey = _bucketBy == EvaluationContext.TargetingKeyAttribute;
        _cumulative = new int[source.Variants.Count];
        _variantIndex = new int[source.Variants.Count];
        var total = 0;
        for (var i = 0; i < source.Variants.Count; i++)
        {
            total += source.Variants[i].Weight;
            _cumulative[i] = total;
            _variantIndex[i] = indexOf(source.Variants[i].Variant);
        }
    }

    /// <summary>Picks a variant index, or -1 when the bucketing value is missing from the context.</summary>
    public int Pick(EvaluationContext context)
    {
        var value = _bucketByTargetingKey ? context.TargetingKey : ToBucketValue(context.GetAttribute(_bucketBy));
        if (value is null)
        {
            return -1;
        }

        var bucket = Bucket(value);
        for (var i = 0; i < _cumulative.Length; i++)
        {
            if (bucket < _cumulative[i])
            {
                return _variantIndex[i];
            }
        }

        return _variantIndex[^1];
    }

    private int Bucket(string value)
    {
        var max = _prefix.Length + Encoding.UTF8.GetMaxByteCount(value.Length);
        byte[]? rented = null;
        Span<byte> buffer = max <= 256 ? stackalloc byte[256] : (rented = ArrayPool<byte>.Shared.Rent(max));
        try
        {
            _prefix.CopyTo(buffer);
            var length = _prefix.Length + Encoding.UTF8.GetBytes(value, buffer[_prefix.Length..]);
            return (int)(Murmur3.Hash32(buffer[..length]) % Rollout.TotalWeight);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private string? ToBucketValue(object? value) => value switch
    {
        null => null,
        string s => s.Length == 0 ? null : (_bucketBy == "country" ? s.ToUpperInvariant() : s),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}

internal sealed class CompiledServe
{
    public CompiledServe(int variantIndex, CompiledRollout? rollout)
    {
        VariantIndex = variantIndex;
        Rollout = rollout;
    }

    public int VariantIndex { get; }

    public CompiledRollout? Rollout { get; }

    public bool IsSplit => Rollout is not null;

    /// <summary>Returns the variant index, or -1 if a rollout cannot bucket this context.</summary>
    public int Pick(EvaluationContext context) => Rollout is null ? VariantIndex : Rollout.Pick(context);
}

internal sealed class CompiledRule
{
    private readonly CompiledCondition[] _conditions;

    public CompiledRule(string id, CompiledCondition[] conditions, CompiledServe serve)
    {
        Id = id;
        _conditions = conditions;
        Serve = serve;
    }

    public string Id { get; }

    public CompiledServe Serve { get; }

    public bool Matches(EvaluationContext context)
    {
        foreach (var condition in _conditions)
        {
            if (!condition.Evaluate(context, 0))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed class CompiledFlag
{
    public CompiledFlag(
        string key,
        FlagType type,
        CompiledVariant[] variants,
        bool enabled,
        int offVariant,
        CompiledRule[] rules,
        CompiledServe fallthrough)
    {
        Key = key;
        Type = type;
        Variants = variants;
        Enabled = enabled;
        OffVariant = offVariant;
        Rules = rules;
        Fallthrough = fallthrough;
    }

    public string Key { get; }

    public FlagType Type { get; }

    public CompiledVariant[] Variants { get; }

    public bool Enabled { get; }

    public int OffVariant { get; }

    public CompiledRule[] Rules { get; }

    public CompiledServe Fallthrough { get; }

    public static bool ValueMatchesType(FlagType type, JsonElement value) => type switch
    {
        FlagType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        FlagType.String => value.ValueKind == JsonValueKind.String,
        FlagType.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        FlagType.Double => value.ValueKind == JsonValueKind.Number,
        FlagType.Object => value.ValueKind is JsonValueKind.Object or JsonValueKind.Array,
        _ => false,
    };
}
