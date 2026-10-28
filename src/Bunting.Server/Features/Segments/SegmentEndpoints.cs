using Bunting.Domain;
using Bunting.Server.Data;
using Bunting.Server.Features.Rulesets;
using Bunting.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Bunting.Server.Features.Segments;

public sealed record SegmentRequest(string? Key, List<string>? Included, List<string>? Excluded, List<Condition>? Conditions);

public sealed record SegmentDto(
    Guid Id,
    string Key,
    IReadOnlyList<string> Included,
    IReadOnlyList<string> Excluded,
    IReadOnlyList<Condition> Conditions,
    long Version);

public static class SegmentEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{projectKey}/environments/{environmentKey}/segments");
        g.MapGet("/", List).RequireAuthorization(ManagementPolicies.Viewer);
        g.MapGet("/{segmentKey}", Get).RequireAuthorization(ManagementPolicies.Viewer);
        g.MapPost("/", Create).RequireAuthorization(ManagementPolicies.Editor);
        g.MapPut("/{segmentKey}", Update).RequireAuthorization(ManagementPolicies.Editor);
        g.MapDelete("/{segmentKey}", Delete).RequireAuthorization(ManagementPolicies.Editor);
    }

    private static SegmentDto ToDto(Segment s) => new(s.Id, s.Key, s.Included, s.Excluded, s.Conditions, s.Version);

    private static async Task<IResult> List(string projectKey, string environmentKey, BuntingDbContext db, CancellationToken ct)
    {
        try
        {
            var project = await db.RequireProjectAsync(projectKey, ct);
            var env = await db.RequireEnvironmentAsync(project.Id, environmentKey, ct);
            var segments = await db.Segments.AsNoTracking().Where(s => s.EnvironmentId == env.Id).OrderBy(s => s.Key).ToListAsync(ct);
            return Results.Ok(segments.Select(ToDto));
        }
        catch (ApiException e)
        {
            return e.Result;
        }
    }

    private static async Task<IResult> Get(
        string projectKey, string environmentKey, string segmentKey, HttpContext http, BuntingDbContext db, CancellationToken ct)
    {
        try
        {
            var project = await db.RequireProjectAsync(projectKey, ct);
            var env = await db.RequireEnvironmentAsync(project.Id, environmentKey, ct);
            var segment = await db.Segments.AsNoTracking().SingleOrDefaultAsync(s => s.EnvironmentId == env.Id && s.Key == segmentKey, ct);
            if (segment is null)
            {
                return Problems.NotFound($"Segment '{segmentKey}' was not found.");
            }

            http.Response.Headers.ETag = ETags.Format(segment.Version);
            return Results.Ok(ToDto(segment));
        }
        catch (ApiException e)
        {
            return e.Result;
        }
    }

    /// <summary>Validates a candidate segment together with its siblings using the real compiler (cycles, unknown references, operands).</summary>
    private static async Task ValidateAsync(BuntingDbContext db, Guid environmentId, Segment candidate, CancellationToken ct)
    {
        var siblings = await db.Segments.AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId && s.Key != candidate.Key)
            .ToListAsync(ct);
        var problems = RulesetSnapshotBuilder.Validate(
            [],
            [.. siblings.Select(RulesetSnapshotBuilder.ToSnapshot), RulesetSnapshotBuilder.ToSnapshot(candidate)],
            "segment");
        if (problems.Count > 0)
        {
            throw Problems.Throw(Problems.Validation(problems));
        }
    }

    private static Task<IResult> Create(
        string projectKey, string environmentKey, SegmentRequest request, HttpContext http, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                if (request.Key is not { } key || !FlagKey.IsValid(key))
                {
                    throw Problems.Throw(Problems.Validation("key", $"Key must match {FlagKey.Pattern}."));
                }

                var project = await db.RequireProjectAsync(projectKey, ct);
                var env = await db.RequireEnvironmentAsync(project.Id, environmentKey, ct);
                if (await db.Segments.AnyAsync(s => s.EnvironmentId == env.Id && s.Key == key, ct))
                {
                    throw Problems.Throw(Problems.Conflict($"Segment '{key}' already exists."));
                }

                var segment = new Segment
                {
                    Id = Guid.CreateVersion7(),
                    EnvironmentId = env.Id,
                    Key = key,
                    Included = [.. (request.Included ?? []).Distinct(StringComparer.Ordinal)],
                    Excluded = [.. (request.Excluded ?? []).Distinct(StringComparer.Ordinal)],
                    Conditions = request.Conditions ?? [],
                    Version = 1,
                };
                await ValidateAsync(db, env.Id, segment, ct);
                db.Segments.Add(segment);
                log.Audit(project.Id, env.Id, "segment", segment.Key, "created", null, ToDto(segment));
                await log.BumpEnvironmentAsync(env.Id, ct);
                http.Response.Headers.ETag = ETags.Format(segment.Version);
                return Results.Created($"/api/v1/projects/{project.Key}/environments/{env.Key}/segments/{segment.Key}", ToDto(segment));
            },
            ct);

    private static Task<IResult> Update(
        string projectKey, string environmentKey, string segmentKey, SegmentRequest request,
        HttpContext http, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                var expected = ETags.RequireIfMatch(http.Request);
                var project = await db.RequireProjectAsync(projectKey, ct);
                var env = await db.RequireEnvironmentAsync(project.Id, environmentKey, ct);
                var segment = await db.Segments.SingleOrDefaultAsync(s => s.EnvironmentId == env.Id && s.Key == segmentKey, ct)
                    ?? throw Problems.Throw(Problems.NotFound($"Segment '{segmentKey}' was not found."));
                ETags.Check(expected, segment.Version);

                var before = ToDto(segment);
                var candidate = new Segment
                {
                    Key = segment.Key,
                    EnvironmentId = env.Id,
                    Included = [.. (request.Included ?? []).Distinct(StringComparer.Ordinal)],
                    Excluded = [.. (request.Excluded ?? []).Distinct(StringComparer.Ordinal)],
                    Conditions = request.Conditions ?? [],
                };
                await ValidateAsync(db, env.Id, candidate, ct);

                segment.Included = candidate.Included;
                segment.Excluded = candidate.Excluded;
                segment.Conditions = candidate.Conditions;
                segment.Version++;
                log.Audit(project.Id, env.Id, "segment", segment.Key, "updated", before, ToDto(segment));
                await log.BumpEnvironmentAsync(env.Id, ct);
                http.Response.Headers.ETag = ETags.Format(segment.Version);
                return Results.Ok(ToDto(segment));
            },
            ct);

    private static Task<IResult> Delete(
        string projectKey, string environmentKey, string segmentKey, HttpContext http, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                var expected = ETags.RequireIfMatch(http.Request);
                var project = await db.RequireProjectAsync(projectKey, ct);
                var env = await db.RequireEnvironmentAsync(project.Id, environmentKey, ct);
                var segment = await db.Segments.SingleOrDefaultAsync(s => s.EnvironmentId == env.Id && s.Key == segmentKey, ct)
                    ?? throw Problems.Throw(Problems.NotFound($"Segment '{segmentKey}' was not found."));
                ETags.Check(expected, segment.Version);

                static bool References(IEnumerable<Condition> conditions, string key) =>
                    conditions.Any(c => c.Operator == ConditionOperator.InSegment && (c.Values ?? []).Contains(key));

                var flags = await db.Flags.AsNoTracking().Where(f => f.ProjectId == project.Id).ToDictionaryAsync(f => f.Id, f => f.Key, ct);
                var usedBy = (await db.FlagConfigs.AsNoTracking().Where(c => c.EnvironmentId == env.Id).ToListAsync(ct))
                    .Where(c => c.Rules.Any(r => References(r.Conditions ?? [], segment.Key)))
                    .Select(c => $"flag:{flags[c.FlagId]}")
                    .ToList();
                usedBy.AddRange((await db.Segments.AsNoTracking().Where(s => s.EnvironmentId == env.Id && s.Id != segment.Id).ToListAsync(ct))
                    .Where(s => References(s.Conditions, segment.Key))
                    .Select(s => $"segment:{s.Key}"));
                if (usedBy.Count > 0)
                {
                    throw Problems.Throw(Problems.Conflict(
                        $"Segment '{segment.Key}' is still referenced and cannot be deleted.",
                        new Dictionary<string, object?> { ["referencedBy"] = usedBy }));
                }

                log.Audit(project.Id, env.Id, "segment", segment.Key, "deleted", ToDto(segment), null);
                db.Segments.Remove(segment);
                await log.BumpEnvironmentAsync(env.Id, ct);
                return Results.NoContent();
            },
            ct);
}
