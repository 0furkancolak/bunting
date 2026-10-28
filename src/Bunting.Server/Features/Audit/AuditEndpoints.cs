using System.Text.Json;
using Bunting.Server.Data;
using Bunting.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Bunting.Server.Features.Audit;

public sealed record AuditEntryDto(
    Guid Id,
    DateTimeOffset Timestamp,
    string Actor,
    string EntityType,
    string EntityId,
    string Action,
    string? Environment,
    JsonElement? Before,
    JsonElement? After);

public sealed record AuditPage(IReadOnlyList<AuditEntryDto> Items, DateTimeOffset? NextBefore);

public static class AuditEndpoints
{
    public static void Map(RouteGroupBuilder api) =>
        api.MapGet("/projects/{projectKey}/audit", Query).RequireAuthorization(ManagementPolicies.Viewer);

    private static async Task<IResult> Query(
        string projectKey,
        string? entityType,
        string? entityId,
        string? actor,
        string? environment,
        DateTimeOffset? from,
        DateTimeOffset? to,
        DateTimeOffset? before,
        int? limit,
        BuntingDbContext db,
        CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Key == projectKey, ct);
        if (project is null)
        {
            return Problems.NotFound($"Project '{projectKey}' was not found.");
        }

        var envs = await db.Environments.AsNoTracking().Where(e => e.ProjectId == project.Id).ToDictionaryAsync(e => e.Id, e => e.Key, ct);
        var query = db.AuditEntries.AsNoTracking().Where(a => a.ProjectId == project.Id);
        if (!string.IsNullOrEmpty(entityType))
        {
            query = query.Where(a => a.EntityType == entityType);
        }

        if (!string.IsNullOrEmpty(entityId))
        {
            query = query.Where(a => a.EntityId == entityId);
        }

        if (!string.IsNullOrEmpty(actor))
        {
            query = query.Where(a => a.Actor == actor);
        }

        if (!string.IsNullOrEmpty(environment))
        {
            var envId = envs.FirstOrDefault(kv => kv.Value == environment).Key;
            query = query.Where(a => a.EnvironmentId == envId);
        }

        if (from is { } f)
        {
            query = query.Where(a => a.Timestamp >= f);
        }

        if (to is { } t)
        {
            query = query.Where(a => a.Timestamp <= t);
        }

        if (before is { } b)
        {
            query = query.Where(a => a.Timestamp < b);
        }

        var take = Math.Clamp(limit ?? 50, 1, 200);
        var rows = await query.OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id).Take(take + 1).ToListAsync(ct);
        var items = rows.Take(take).Select(a => new AuditEntryDto(
            a.Id,
            a.Timestamp,
            a.Actor,
            a.EntityType,
            a.EntityId,
            a.Action,
            a.EnvironmentId is { } id && envs.TryGetValue(id, out var key) ? key : null,
            a.BeforeJson is null ? null : JsonDocument.Parse(a.BeforeJson).RootElement.Clone(),
            a.AfterJson is null ? null : JsonDocument.Parse(a.AfterJson).RootElement.Clone())).ToList();
        return Results.Ok(new AuditPage(items, rows.Count > take ? items[^1].Timestamp : null));
    }
}
