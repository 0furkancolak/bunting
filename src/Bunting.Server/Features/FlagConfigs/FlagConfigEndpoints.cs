using System.Security.Cryptography;
using Bunting.Domain;
using Bunting.Server.Data;
using Bunting.Server.Features.Rulesets;
using Bunting.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Bunting.Server.Features.FlagConfigs;

public sealed record FlagConfigRequest(bool Enabled, string OffVariant, List<Rule>? Rules, Serve Fallthrough);

public sealed record SetEnabledRequest(bool Enabled);

public sealed record FlagConfigDto(
    string FlagKey,
    string Environment,
    bool Enabled,
    string OffVariant,
    IReadOnlyList<Rule> Rules,
    Serve Fallthrough,
    long Version);

public static class FlagConfigEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{projectKey}/environments/{environmentKey}/flags/{flagKey}");
        g.MapGet("/", Get).RequireAuthorization(ManagementPolicies.Viewer);
        g.MapPut("/", Put).RequireAuthorization(ManagementPolicies.Editor);
        g.MapPatch("/", SetEnabled).RequireAuthorization(ManagementPolicies.Editor);
    }

    private static FlagConfigDto ToDto(Flag f, ProjectEnvironment e, FlagEnvironmentConfig c) =>
        new(f.Key, e.Key, c.Enabled, c.OffVariant, c.Rules, c.Fallthrough, c.Version);

    private static async Task<(Project, ProjectEnvironment, Flag, FlagEnvironmentConfig)> LoadAsync(
        BuntingDbContext db, string projectKey, string environmentKey, string flagKey, CancellationToken ct)
    {
        var project = await db.RequireProjectAsync(projectKey, ct);
        var env = await db.RequireEnvironmentAsync(project.Id, environmentKey, ct);
        var flag = await db.RequireFlagAsync(project.Id, flagKey, ct);
        var config = await db.FlagConfigs.SingleOrDefaultAsync(c => c.FlagId == flag.Id && c.EnvironmentId == env.Id, ct)
            ?? throw Problems.Throw(Problems.NotFound($"Flag '{flagKey}' has no configuration in '{environmentKey}'."));
        return (project, env, flag, config);
    }

    private static async Task<IResult> Get(
        string projectKey, string environmentKey, string flagKey, HttpContext http, BuntingDbContext db, CancellationToken ct)
    {
        try
        {
            var (_, env, flag, config) = await LoadAsync(db, projectKey, environmentKey, flagKey, ct);
            http.Response.Headers.ETag = ETags.Format(config.Version);
            return Results.Ok(ToDto(flag, env, config));
        }
        catch (ApiException e)
        {
            return e.Result;
        }
    }

    private static string NewRuleId() => "r_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    private static Task<IResult> Put(
        string projectKey, string environmentKey, string flagKey, FlagConfigRequest request,
        HttpContext http, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                var expected = ETags.RequireIfMatch(http.Request);
                var (project, env, flag, config) = await LoadAsync(db, projectKey, environmentKey, flagKey, ct);
                ETags.Check(expected, config.Version);

                var rules = (request.Rules ?? [])
                    .Select(r => r with
                    {
                        Id = string.IsNullOrWhiteSpace(r.Id) ? NewRuleId() : r.Id.Trim(),
                        Conditions = r.Conditions ?? [],
                    })
                    .ToList();
                var errors = new Dictionary<string, string[]>();
                var duplicates = rules.GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
                if (duplicates.Length > 0)
                {
                    errors["rules"] = [$"Rule ids must be unique: {string.Join(", ", duplicates)}."];
                }

                var candidate = new FlagEnvironmentConfig
                {
                    FlagId = flag.Id,
                    EnvironmentId = env.Id,
                    Enabled = request.Enabled,
                    OffVariant = request.OffVariant ?? string.Empty,
                    Rules = rules,
                    Fallthrough = request.Fallthrough ?? new Serve(null, null),
                };
                var segments = await db.Segments.AsNoTracking().Where(s => s.EnvironmentId == env.Id).ToListAsync(ct);
                foreach (var (field, messages) in RulesetSnapshotBuilder.Validate(
                    [RulesetSnapshotBuilder.ToSnapshot(flag, candidate)],
                    [.. segments.Select(RulesetSnapshotBuilder.ToSnapshot)],
                    "config"))
                {
                    errors[field] = messages;
                }

                if (errors.Count > 0)
                {
                    throw Problems.Throw(Problems.Validation(errors));
                }

                var before = ToDto(flag, env, config);
                config.Enabled = candidate.Enabled;
                config.OffVariant = candidate.OffVariant;
                config.Rules = candidate.Rules;
                config.Fallthrough = candidate.Fallthrough;
                config.Version++;

                var after = ToDto(flag, env, config);
                log.Audit(project.Id, env.Id, "flag-config", flag.Key, "updated", before, after);
                await log.BumpEnvironmentAsync(env.Id, ct);
                http.Response.Headers.ETag = ETags.Format(config.Version);
                return Results.Ok(after);
            },
            ct);

    /// <summary>Kill switch: toggles <c>Enabled</c> only.</summary>
    private static Task<IResult> SetEnabled(
        string projectKey, string environmentKey, string flagKey, SetEnabledRequest request,
        HttpContext http, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                var expected = ETags.RequireIfMatch(http.Request);
                var (project, env, flag, config) = await LoadAsync(db, projectKey, environmentKey, flagKey, ct);
                ETags.Check(expected, config.Version);

                var before = ToDto(flag, env, config);
                config.Enabled = request.Enabled;
                config.Version++;
                var after = ToDto(flag, env, config);
                log.Audit(project.Id, env.Id, "flag-config", flag.Key, request.Enabled ? "enabled" : "disabled", before, after);
                await log.BumpEnvironmentAsync(env.Id, ct);
                http.Response.Headers.ETag = ETags.Format(config.Version);
                return Results.Ok(after);
            },
            ct);
}
