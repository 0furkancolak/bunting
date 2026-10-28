using Bunting.Domain;
using Bunting.Server.Data;
using Bunting.Server.Features.Projects;
using Bunting.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Bunting.Server.Features.Environments;

public sealed record CreateEnvironmentRequest(string Key, string Name);

public sealed record UpdateEnvironmentRequest(string Name);

public static class EnvironmentEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{projectKey}/environments");
        g.MapGet("/", List).RequireAuthorization(ManagementPolicies.Viewer);
        g.MapGet("/{environmentKey}", Get).RequireAuthorization(ManagementPolicies.Viewer);
        g.MapPost("/", Create).RequireAuthorization(ManagementPolicies.Owner);
        g.MapPatch("/{environmentKey}", Rename).RequireAuthorization(ManagementPolicies.Owner);
        g.MapDelete("/{environmentKey}", Delete).RequireAuthorization(ManagementPolicies.Owner);
    }

    private static async Task<IResult> List(string projectKey, BuntingDbContext db, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Key == projectKey, ct);
        if (project is null)
        {
            return Problems.NotFound($"Project '{projectKey}' was not found.");
        }

        var envs = await db.Environments.AsNoTracking().Where(e => e.ProjectId == project.Id).OrderBy(e => e.Position).ToListAsync(ct);
        return Results.Ok(envs.Select(ProjectEndpoints.ToDto));
    }

    private static async Task<IResult> Get(string projectKey, string environmentKey, BuntingDbContext db, CancellationToken ct)
    {
        var env = await db.Environments.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Key == environmentKey && db.Projects.Any(p => p.Id == e.ProjectId && p.Key == projectKey), ct);
        return env is null ? Problems.NotFound($"Environment '{environmentKey}' was not found.") : Results.Ok(ProjectEndpoints.ToDto(env));
    }

    private static Task<IResult> Create(string projectKey, CreateEnvironmentRequest request, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                if (!Lookups.IsValidScopeKey(request.Key))
                {
                    throw Problems.Throw(Problems.Validation("key", "Key must match ^[a-z0-9][a-z0-9-]{0,62}$."));
                }

                if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
                {
                    throw Problems.Throw(Problems.Validation("name", "Name is required and at most 200 characters."));
                }

                var project = await db.RequireProjectAsync(projectKey, ct);
                if (await db.Environments.AnyAsync(e => e.ProjectId == project.Id && e.Key == request.Key, ct))
                {
                    throw Problems.Throw(Problems.Conflict($"Environment '{request.Key}' already exists."));
                }

                var env = new ProjectEnvironment
                {
                    Id = Guid.CreateVersion7(),
                    ProjectId = project.Id,
                    Key = request.Key,
                    Name = request.Name.Trim(),
                    RulesetVersion = 1,
                    Position = await db.Environments.Where(e => e.ProjectId == project.Id).MaxAsync(e => (int?)e.Position, ct) + 1 ?? 0,
                };
                db.Environments.Add(env);

                // Every flag exists in every environment, switched off. Reuse the off variant other environments already use.
                var flags = await db.Flags.AsNoTracking().Where(f => f.ProjectId == project.Id).ToListAsync(ct);
                var existingOff = await db.FlagConfigs.AsNoTracking()
                    .Where(c => flags.Select(f => f.Id).Contains(c.FlagId))
                    .Select(c => new { c.FlagId, c.OffVariant })
                    .ToListAsync(ct);
                foreach (var flag in flags)
                {
                    var off = existingOff.FirstOrDefault(x => x.FlagId == flag.Id)?.OffVariant ?? flag.Variants.Keys.First();
                    db.FlagConfigs.Add(DefaultConfig(flag.Id, env.Id, off));
                }

                log.Audit(project.Id, env.Id, "environment", env.Key, "created", null, new { env.Key, env.Name });
                return Results.Created($"/api/v1/projects/{project.Key}/environments/{env.Key}", ProjectEndpoints.ToDto(env));
            },
            ct);

    public static FlagEnvironmentConfig DefaultConfig(Guid flagId, Guid environmentId, string offVariant) => new()
    {
        FlagId = flagId,
        EnvironmentId = environmentId,
        Enabled = false,
        OffVariant = offVariant,
        Rules = [],
        Fallthrough = Serve.Single(offVariant),
        Version = 1,
    };

    private static Task<IResult> Rename(
        string projectKey, string environmentKey, UpdateEnvironmentRequest request, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
                {
                    throw Problems.Throw(Problems.Validation("name", "Name is required and at most 200 characters."));
                }

                var project = await db.RequireProjectAsync(projectKey, ct);
                var env = await db.RequireEnvironmentAsync(project.Id, environmentKey, ct);
                var before = new { env.Key, env.Name };
                env.Name = request.Name.Trim();
                log.Audit(project.Id, env.Id, "environment", env.Key, "updated", before, new { env.Key, env.Name });
                return Results.Ok(ProjectEndpoints.ToDto(env));
            },
            ct);

    private static Task<IResult> Delete(string projectKey, string environmentKey, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                var project = await db.RequireProjectAsync(projectKey, ct);
                var env = await db.RequireEnvironmentAsync(project.Id, environmentKey, ct);
                log.Audit(project.Id, env.Id, "environment", env.Key, "deleted", new { env.Key, env.Name }, null);
                db.Environments.Remove(env);
                return Results.NoContent();
            },
            ct);
}
