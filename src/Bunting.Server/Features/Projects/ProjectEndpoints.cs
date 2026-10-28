using Bunting.Domain;
using Bunting.Server.Data;
using Bunting.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Bunting.Server.Features.Projects;

public sealed record CreateProjectRequest(string Key, string Name);

public sealed record UpdateProjectRequest(string Name);

public sealed record EnvironmentDto(Guid Id, string Key, string Name, long RulesetVersion);

public sealed record ProjectDto(Guid Id, string Key, string Name, IReadOnlyList<EnvironmentDto> Environments);

public static class ProjectEndpoints
{
    public static readonly string[] DefaultEnvironments = ["development", "staging", "production"];

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects");
        g.MapGet("/", List).RequireAuthorization(ManagementPolicies.Viewer);
        g.MapGet("/{projectKey}", Get).RequireAuthorization(ManagementPolicies.Viewer);
        g.MapPost("/", Create).RequireAuthorization(ManagementPolicies.Owner);
        g.MapPatch("/{projectKey}", Rename).RequireAuthorization(ManagementPolicies.Owner);
        g.MapDelete("/{projectKey}", Delete).RequireAuthorization(ManagementPolicies.Owner);
    }

    public static EnvironmentDto ToDto(ProjectEnvironment e) => new(e.Id, e.Key, e.Name, e.RulesetVersion);

    private static ProjectDto ToDto(Project p, IEnumerable<ProjectEnvironment> envs) =>
        new(p.Id, p.Key, p.Name, [.. envs.OrderBy(e => e.Position).Select(ToDto)]);

    private static async Task<IResult> List(BuntingDbContext db, CancellationToken ct)
    {
        var projects = await db.Projects.AsNoTracking().OrderBy(p => p.Key).ToListAsync(ct);
        var envs = await db.Environments.AsNoTracking().ToListAsync(ct);
        return Results.Ok(projects.Select(p => ToDto(p, envs.Where(e => e.ProjectId == p.Id))));
    }

    private static async Task<IResult> Get(string projectKey, BuntingDbContext db, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Key == projectKey, ct);
        if (project is null)
        {
            return Problems.NotFound($"Project '{projectKey}' was not found.");
        }

        var envs = await db.Environments.AsNoTracking().Where(e => e.ProjectId == project.Id).ToListAsync(ct);
        return Results.Ok(ToDto(project, envs));
    }

    private static Task<IResult> Create(CreateProjectRequest request, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                var errors = new Dictionary<string, string[]>();
                if (!Lookups.IsValidScopeKey(request.Key))
                {
                    errors["key"] = ["Key must match ^[a-z0-9][a-z0-9-]{0,62}$."];
                }

                if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
                {
                    errors["name"] = ["Name is required and at most 200 characters."];
                }

                if (errors.Count > 0)
                {
                    throw Problems.Throw(Problems.Validation(errors));
                }

                if (await db.Projects.AnyAsync(p => p.Key == request.Key, ct))
                {
                    throw Problems.Throw(Problems.Conflict($"Project '{request.Key}' already exists."));
                }

                var project = new Project { Id = Guid.CreateVersion7(), Key = request.Key, Name = request.Name.Trim() };
                db.Projects.Add(project);
                var envs = DefaultEnvironments
                    .Select((key, index) => new ProjectEnvironment { Id = Guid.CreateVersion7(), ProjectId = project.Id, Key = key, Name = key, RulesetVersion = 1, Position = index })
                    .ToList();
                db.Environments.AddRange(envs);
                log.Audit(project.Id, null, "project", project.Key, "created", null, new { project.Key, project.Name });
                await Task.CompletedTask;
                return Results.Created($"/api/v1/projects/{project.Key}", ToDto(project, envs));
            },
            ct);

    private static Task<IResult> Rename(string projectKey, UpdateProjectRequest request, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
                {
                    throw Problems.Throw(Problems.Validation("name", "Name is required and at most 200 characters."));
                }

                var project = await db.RequireProjectAsync(projectKey, ct);
                var before = new { project.Key, project.Name };
                project.Name = request.Name.Trim();
                log.Audit(project.Id, null, "project", project.Key, "updated", before, new { project.Key, project.Name });
                var envs = await db.Environments.AsNoTracking().Where(e => e.ProjectId == project.Id).ToListAsync(ct);
                return Results.Ok(ToDto(project, envs));
            },
            ct);

    private static Task<IResult> Delete(string projectKey, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                var project = await db.RequireProjectAsync(projectKey, ct);
                log.Audit(project.Id, null, "project", project.Key, "deleted", new { project.Key, project.Name }, null);
                db.Projects.Remove(project);
                return Results.NoContent();
            },
            ct);
}
