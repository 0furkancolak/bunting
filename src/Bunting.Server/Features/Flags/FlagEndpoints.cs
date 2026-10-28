using System.Text.Json;
using Bunting.Domain;
using Bunting.Evaluation;
using Bunting.Server.Data;
using Bunting.Server.Features.Environments;
using Bunting.Server.Features.Rulesets;
using Bunting.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Bunting.Server.Features.Flags;

public sealed record CreateFlagRequest(
    string Key,
    string Name,
    string? Description,
    FlagType Type,
    Dictionary<string, JsonElement> Variants,
    string? DefaultVariant,
    List<string>? Tags,
    bool ClientSideAvailable);

public sealed record UpdateFlagRequest(
    string Name,
    string? Description,
    Dictionary<string, JsonElement> Variants,
    List<string>? Tags,
    bool ClientSideAvailable,
    bool Archived);

public sealed record FlagEnvironmentState(bool Enabled, long Version);

public sealed record FlagDto(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    FlagType Type,
    Dictionary<string, JsonElement> Variants,
    List<string> Tags,
    bool Archived,
    bool ClientSideAvailable,
    long Version,
    Dictionary<string, FlagEnvironmentState>? Environments = null);

public static class FlagEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{projectKey}/flags");
        g.MapGet("/", List).RequireAuthorization(ManagementPolicies.Viewer);
        g.MapGet("/{flagKey}", Get).RequireAuthorization(ManagementPolicies.Viewer);
        g.MapPost("/", Create).RequireAuthorization(ManagementPolicies.Editor);
        g.MapPut("/{flagKey}", Update).RequireAuthorization(ManagementPolicies.Editor);
        g.MapDelete("/{flagKey}", Delete).RequireAuthorization(ManagementPolicies.Editor);
    }

    public static FlagDto ToDto(Flag f, Dictionary<string, FlagEnvironmentState>? envs = null) =>
        new(f.Id, f.Key, f.Name, f.Description, f.Type, f.Variants, f.Tags, f.Archived, f.ClientSideAvailable, f.Version, envs);

    private static async Task<IResult> List(
        string projectKey, string? search, string? tag, bool? archived, BuntingDbContext db, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Key == projectKey, ct);
        if (project is null)
        {
            return Problems.NotFound($"Project '{projectKey}' was not found.");
        }

        var query = db.Flags.AsNoTracking().Where(f => f.ProjectId == project.Id);
        if (archived is { } a)
        {
            query = query.Where(f => f.Archived == a);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(f => EF.Functions.ILike(f.Key, pattern) || EF.Functions.ILike(f.Name, pattern));
        }

        var flags = await query.OrderBy(f => f.Key).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(tag))
        {
            flags = [.. flags.Where(f => f.Tags.Contains(tag, StringComparer.Ordinal))];
        }

        var envs = await db.Environments.AsNoTracking().Where(e => e.ProjectId == project.Id).ToListAsync(ct);
        var ids = flags.Select(f => f.Id).ToList();
        var configs = await db.FlagConfigs.AsNoTracking().Where(c => ids.Contains(c.FlagId)).ToListAsync(ct);
        return Results.Ok(flags.Select(f => ToDto(
            f,
            configs.Where(c => c.FlagId == f.Id)
                .ToDictionary(c => envs.First(e => e.Id == c.EnvironmentId).Key, c => new FlagEnvironmentState(c.Enabled, c.Version)))));
    }

    private static async Task<IResult> Get(string projectKey, string flagKey, HttpContext http, BuntingDbContext db, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Key == projectKey, ct);
        if (project is null)
        {
            return Problems.NotFound($"Project '{projectKey}' was not found.");
        }

        var flag = await db.Flags.AsNoTracking().SingleOrDefaultAsync(f => f.ProjectId == project.Id && f.Key == flagKey, ct);
        if (flag is null)
        {
            return Problems.NotFound($"Flag '{flagKey}' was not found.");
        }

        http.Response.Headers.ETag = ETags.Format(flag.Version);
        return Results.Ok(ToDto(flag));
    }

    private static Dictionary<string, string[]> ValidateBasics(string key, string name, string? description, Dictionary<string, JsonElement>? variants)
    {
        var errors = new Dictionary<string, string[]>();
        if (!FlagKey.IsValid(key))
        {
            errors["key"] = [$"Key must match {FlagKey.Pattern}."];
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            errors["name"] = ["Name is required and at most 200 characters."];
        }

        if (description is { Length: > 2000 })
        {
            errors["description"] = ["Description must be at most 2000 characters."];
        }

        if (variants is not { Count: > 0 })
        {
            errors["variants"] = ["At least one variant is required."];
        }
        else if (variants.Keys.Any(k => string.IsNullOrWhiteSpace(k) || k.Length > 100))
        {
            errors["variants"] = ["Variant names must be 1-100 characters."];
        }

        return errors;
    }

    private static Task<IResult> Create(
        string projectKey, CreateFlagRequest request, HttpContext http, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                var errors = ValidateBasics(request.Key, request.Name, request.Description, request.Variants);
                if (!Enum.IsDefined(request.Type))
                {
                    errors["type"] = ["Unknown flag type."];
                }

                if (errors.Count == 0)
                {
                    var defaultVariant = request.DefaultVariant ?? request.Variants.Keys.First();
                    var candidate = new Flag { Key = request.Key, Name = request.Name, Type = request.Type, Variants = request.Variants };
                    var config = EnvironmentEndpoints.DefaultConfig(Guid.Empty, Guid.Empty, defaultVariant);
                    foreach (var (field, messages) in RulesetSnapshotBuilder.Validate(
                        [RulesetSnapshotBuilder.ToSnapshot(candidate, config)], [], "variants"))
                    {
                        errors[field] = messages;
                    }
                }

                if (errors.Count > 0)
                {
                    throw Problems.Throw(Problems.Validation(errors));
                }

                var project = await db.RequireProjectAsync(projectKey, ct);
                if (await db.Flags.AnyAsync(f => f.ProjectId == project.Id && f.Key == request.Key, ct))
                {
                    throw Problems.Throw(Problems.Conflict($"Flag '{request.Key}' already exists."));
                }

                var flag = new Flag
                {
                    Id = Guid.CreateVersion7(),
                    ProjectId = project.Id,
                    Key = request.Key,
                    Name = request.Name.Trim(),
                    Description = request.Description,
                    Type = request.Type,
                    Variants = request.Variants,
                    Tags = [.. (request.Tags ?? []).Distinct(StringComparer.Ordinal)],
                    ClientSideAvailable = request.ClientSideAvailable,
                    Version = 1,
                };
                db.Flags.Add(flag);

                var off = request.DefaultVariant ?? request.Variants.Keys.First();
                var envIds = await db.Environments.Where(e => e.ProjectId == project.Id).OrderBy(e => e.Id).Select(e => e.Id).ToListAsync(ct);
                foreach (var envId in envIds)
                {
                    db.FlagConfigs.Add(EnvironmentEndpoints.DefaultConfig(flag.Id, envId, off));
                }

                log.Audit(project.Id, null, "flag", flag.Key, "created", null, ToDto(flag));
                foreach (var envId in envIds)
                {
                    await log.BumpEnvironmentAsync(envId, ct);
                }

                http.Response.Headers.ETag = ETags.Format(flag.Version);
                return Results.Created($"/api/v1/projects/{project.Key}/flags/{flag.Key}", ToDto(flag));
            },
            ct);

    private static Task<IResult> Update(
        string projectKey, string flagKey, UpdateFlagRequest request, HttpContext http, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                var expected = ETags.RequireIfMatch(http.Request);
                var errors = ValidateBasics(flagKey, request.Name, request.Description, request.Variants);
                if (errors.Count > 0)
                {
                    throw Problems.Throw(Problems.Validation(errors));
                }

                var project = await db.RequireProjectAsync(projectKey, ct);
                var flag = await db.RequireFlagAsync(project.Id, flagKey, ct);
                ETags.Check(expected, flag.Version);

                var before = ToDto(flag);
                var rulesetRelevant = flag.Archived != request.Archived
                    || flag.ClientSideAvailable != request.ClientSideAvailable
                    || BuntingJson.Serialize(flag.Variants) != BuntingJson.Serialize(request.Variants);

                // Re-validate every environment's config against the new variants (removed/retyped variants, etc.).
                var envs = await db.Environments.AsNoTracking().Where(e => e.ProjectId == project.Id).OrderBy(e => e.Id).ToListAsync(ct);
                var configs = await db.FlagConfigs.AsNoTracking().Where(c => c.FlagId == flag.Id).ToListAsync(ct);
                var candidate = new Flag { Key = flag.Key, Name = request.Name, Type = flag.Type, Variants = request.Variants, Archived = request.Archived };
                foreach (var env in envs)
                {
                    var config = configs.Single(c => c.EnvironmentId == env.Id);
                    var segments = await db.Segments.AsNoTracking().Where(s => s.EnvironmentId == env.Id).ToListAsync(ct);
                    var problems = RulesetSnapshotBuilder.Validate(
                        [RulesetSnapshotBuilder.ToSnapshot(candidate, config)],
                        [.. segments.Select(RulesetSnapshotBuilder.ToSnapshot)],
                        $"variants[{env.Key}]");
                    foreach (var (field, messages) in problems)
                    {
                        errors[field] = messages;
                    }
                }

                if (errors.Count > 0)
                {
                    throw Problems.Throw(Problems.Validation(errors));
                }

                flag.Name = request.Name.Trim();
                flag.Description = request.Description;
                flag.Variants = request.Variants;
                flag.Tags = [.. (request.Tags ?? []).Distinct(StringComparer.Ordinal)];
                flag.ClientSideAvailable = request.ClientSideAvailable;
                flag.Archived = request.Archived;
                flag.Version++;

                log.Audit(project.Id, null, "flag", flag.Key, request.Archived != before.Archived ? (request.Archived ? "archived" : "unarchived") : "updated", before, ToDto(flag));
                if (rulesetRelevant)
                {
                    foreach (var env in envs)
                    {
                        await log.BumpEnvironmentAsync(env.Id, ct);
                    }
                }

                http.Response.Headers.ETag = ETags.Format(flag.Version);
                return Results.Ok(ToDto(flag));
            },
            ct);

    private static Task<IResult> Delete(
        string projectKey, string flagKey, HttpContext http, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                var expected = ETags.RequireIfMatch(http.Request);
                var project = await db.RequireProjectAsync(projectKey, ct);
                var flag = await db.RequireFlagAsync(project.Id, flagKey, ct);
                ETags.Check(expected, flag.Version);
                if (!flag.Archived)
                {
                    throw Problems.Throw(Problems.Conflict("Only archived flags can be deleted. Archive the flag first."));
                }

                log.Audit(project.Id, null, "flag", flag.Key, "deleted", ToDto(flag), null);
                db.Flags.Remove(flag);
                await log.BumpProjectAsync(project.Id, ct);
                return Results.NoContent();
            },
            ct);
}
