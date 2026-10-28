using Bunting.Domain;
using Bunting.Evaluation;
using Bunting.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Bunting.Server.Features.Rulesets;

/// <summary>Builds the evaluation input of an environment and validates candidate changes with the real compiler.</summary>
public sealed class RulesetSnapshotBuilder(BuntingDbContext db)
{
    public async Task<RulesetSnapshot> BuildAsync(Guid environmentId, CancellationToken ct)
    {
        var environment = await db.Environments.AsNoTracking().SingleAsync(e => e.Id == environmentId, ct);
        var flags = await db.Flags.AsNoTracking().Where(f => f.ProjectId == environment.ProjectId).ToListAsync(ct);
        var configs = (await db.FlagConfigs.AsNoTracking().Where(c => c.EnvironmentId == environmentId).ToListAsync(ct))
            .ToDictionary(c => c.FlagId);
        var segments = await db.Segments.AsNoTracking().Where(s => s.EnvironmentId == environmentId).OrderBy(s => s.Key).ToListAsync(ct);

        return new RulesetSnapshot(
            environment.RulesetVersion,
            [.. flags.OrderBy(f => f.Key, StringComparer.Ordinal).Where(f => configs.ContainsKey(f.Id)).Select(f => ToSnapshot(f, configs[f.Id]))],
            [.. segments.Select(ToSnapshot)]);
    }

    public static FlagSnapshot ToSnapshot(Flag flag, FlagEnvironmentConfig config) =>
        new(flag.Key, flag.Type, flag.Variants, config.Enabled, flag.Archived, config.OffVariant, config.Rules, config.Fallthrough, flag.ClientSideAvailable);

    public static SegmentSnapshot ToSnapshot(Segment s) => new(s.Key, s.Included, s.Excluded, s.Conditions);

    /// <summary>Compiles a candidate and returns every compiler diagnostic as validation errors (empty when valid).</summary>
    public static Dictionary<string, string[]> Validate(IReadOnlyList<FlagSnapshot> flags, IReadOnlyList<SegmentSnapshot> segments, string field)
    {
        var ruleset = CompiledRuleset.Compile(new RulesetSnapshot(0, flags, segments));
        var messages = ruleset.Diagnostics.Select(d => d.Message).Distinct().ToArray();
        return messages.Length == 0 ? [] : new Dictionary<string, string[]> { [field] = messages };
    }
}
