using Bunting.Admin.Api;
using Bunting.Domain;

namespace Bunting.Admin.Editing;

/// <summary>Mutable form model of a <see cref="Condition"/>. Values are edited as text, one per line or comma separated.</summary>
public sealed class ConditionVm
{
    public string Attribute { get; set; } = string.Empty;

    public ConditionOperator Operator { get; set; } = ConditionOperator.Equals;

    public string ValuesText { get; set; } = string.Empty;

    public bool Negate { get; set; }

    public static ConditionVm From(Condition condition) => new()
    {
        Attribute = condition.Attribute,
        Operator = condition.Operator,
        ValuesText = string.Join('\n', condition.Values),
        Negate = condition.Negate,
    };

    public Condition ToCondition() => new(
        Attribute.Trim(),
        Operator,
        [.. ValuesText.Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        Negate);
}

public sealed class WeightVm
{
    public required string Variant { get; set; }

    /// <summary>Share in percent with two decimals (the server stores basis points).</summary>
    public double Percent { get; set; }
}

/// <summary>Mutable form model of a <see cref="Serve"/>: one variant, or a percentage rollout across variants.</summary>
public sealed class ServeVm
{
    public bool IsRollout { get; set; }

    public string? Variant { get; set; }

    public List<WeightVm> Weights { get; set; } = [];

    public string? BucketBy { get; set; }

    public string? Salt { get; set; }

    /// <summary>Weights in basis points as they will be sent to the server.</summary>
    public int TotalBasisPoints => Weights.Sum(w => (int)Math.Round(w.Percent * 100));

    public double TotalPercent => TotalBasisPoints / 100.0;

    public bool IsValid => IsRollout ? TotalBasisPoints == Rollout.TotalWeight : !string.IsNullOrEmpty(Variant);

    public static ServeVm From(Serve serve, IReadOnlyCollection<string> variantKeys)
    {
        var vm = new ServeVm { Variant = serve.Variant, IsRollout = serve.Rollout is not null };
        vm.SetVariants(variantKeys);
        if (serve.Rollout is { } rollout)
        {
            foreach (var weight in rollout.Variants)
            {
                var row = vm.Weights.FirstOrDefault(w => w.Variant == weight.Variant);
                if (row is null)
                {
                    vm.Weights.Add(new WeightVm { Variant = weight.Variant, Percent = weight.Weight / 100.0 });
                }
                else
                {
                    row.Percent = weight.Weight / 100.0;
                }
            }

            vm.BucketBy = rollout.BucketBy;
            vm.Salt = rollout.Salt;
        }

        return vm;
    }

    /// <summary>Makes sure every variant has a weight row (new variants start at 0%).</summary>
    public void SetVariants(IEnumerable<string> variantKeys)
    {
        foreach (var key in variantKeys)
        {
            if (Weights.All(w => w.Variant != key))
            {
                Weights.Add(new WeightVm { Variant = key, Percent = 0 });
            }
        }
    }

    public Serve ToServe() => IsRollout
        ? Serve.Split(new Rollout(
            [.. Weights.Where(w => w.Percent > 0).Select(w => new WeightedVariant(w.Variant, (int)Math.Round(w.Percent * 100)))],
            string.IsNullOrWhiteSpace(BucketBy) ? null : BucketBy.Trim(),
            string.IsNullOrWhiteSpace(Salt) ? null : Salt.Trim()))
        : Serve.Single(Variant ?? string.Empty);
}

public sealed class RuleVm
{
    public string Id { get; set; } = string.Empty;

    public string? Description { get; set; }

    public List<ConditionVm> Conditions { get; set; } = [];

    public ServeVm Serve { get; set; } = new();

    public static RuleVm From(Rule rule, IReadOnlyCollection<string> variantKeys) => new()
    {
        Id = rule.Id,
        Description = rule.Description,
        Conditions = [.. rule.Conditions.Select(ConditionVm.From)],
        Serve = ServeVm.From(rule.Serve, variantKeys),
    };

    public static RuleVm New(IReadOnlyCollection<string> variantKeys, IEnumerable<string> existingIds)
    {
        var taken = existingIds.ToHashSet(StringComparer.Ordinal);
        var n = taken.Count + 1;
        while (taken.Contains($"rule-{n}"))
        {
            n++;
        }

        var serve = new ServeVm { Variant = variantKeys.FirstOrDefault() };
        serve.SetVariants(variantKeys);
        return new RuleVm { Id = $"rule-{n}", Conditions = [new ConditionVm()], Serve = serve };
    }

    public Rule ToRule() => new(Id.Trim(), string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(), [.. Conditions.Select(c => c.ToCondition())], Serve.ToServe());
}

/// <summary>Mutable form model of a flag's configuration in one environment.</summary>
public sealed class ConfigVm
{
    public bool Enabled { get; set; }

    public string OffVariant { get; set; } = string.Empty;

    public List<RuleVm> Rules { get; set; } = [];

    public ServeVm Fallthrough { get; set; } = new();

    public long Version { get; set; }

    public bool IsValid => Fallthrough.IsValid
        && !string.IsNullOrEmpty(OffVariant)
        && Rules.All(r => r.Serve.IsValid && !string.IsNullOrWhiteSpace(r.Id))
        && Rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() == Rules.Count;

    public static ConfigVm From(FlagConfigInfo config, IReadOnlyCollection<string> variantKeys) => new()
    {
        Enabled = config.Enabled,
        OffVariant = config.OffVariant,
        Rules = [.. config.Rules.Select(r => RuleVm.From(r, variantKeys))],
        Fallthrough = ServeVm.From(config.Fallthrough, variantKeys),
        Version = config.Version,
    };

    public FlagConfigRequest ToRequest() => new(Enabled, OffVariant, [.. Rules.Select(r => r.ToRule())], Fallthrough.ToServe());

    public void Move(int index, int offset)
    {
        var target = index + offset;
        if (index < 0 || index >= Rules.Count || target < 0 || target >= Rules.Count)
        {
            return;
        }

        (Rules[index], Rules[target]) = (Rules[target], Rules[index]);
    }
}
