using System.Text.Json;
using Bunting.Evaluation;
using Bunting.Server.Data;
using Bunting.Server.Features.Ofrep;
using Bunting.Server.Features.Rulesets;
using Bunting.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bunting.Server.Features.Playground;

public sealed record PlaygroundResult(
    string FlagKey,
    JsonElement? Value,
    string? Variant,
    string Reason,
    string? ErrorCode,
    string? RuleId);

public sealed record PlaygroundResponse(long RulesetVersion, IReadOnlyList<PlaygroundResult> Results);

/// <summary>
/// Evaluation playground: runs the real engine against an environment's current ruleset with a context supplied by
/// a human. Contexts are evaluated in memory and never logged or stored.
/// </summary>
public static class PlaygroundEndpoints
{
    public static void Map(RouteGroupBuilder api) =>
        api.MapPost("/projects/{projectKey}/environments/{environmentKey}/evaluate", Evaluate).RequireAuthorization(ManagementPolicies.Viewer);

    private static async Task<IResult> Evaluate(
        string projectKey,
        string environmentKey,
        HttpContext http,
        BuntingDbContext db,
        RulesetCache cache,
        IOptions<OfrepOptions> options,
        CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Key == projectKey, ct);
        var environment = project is null
            ? null
            : await db.Environments.AsNoTracking().SingleOrDefaultAsync(e => e.ProjectId == project.Id && e.Key == environmentKey, ct);
        if (environment is null)
        {
            return Problems.NotFound($"Environment '{environmentKey}' of project '{projectKey}' was not found.");
        }

        JsonElement body;
        try
        {
            body = (await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct)).RootElement.Clone();
        }
        catch (JsonException)
        {
            return Problems.Validation("body", "The request body must be JSON.");
        }

        if (!OfrepEndpoints.TryReadContext(body, http, options.Value, out var context, out var details))
        {
            return Problems.Validation("context", details!);
        }

        var flagKey = body.TryGetProperty("flagKey", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString() : null;
        var cached = await cache.GetAsync(environment.Id, ct);
        var keys = flagKey is null ? cached.Ruleset.Flags.Where(f => !f.Archived).Select(f => f.Key).ToList() : [flagKey];

        var results = keys.Select(k =>
        {
            var result = cached.Ruleset.Evaluate(k, context);
            return new PlaygroundResult(
                k,
                result.IsError ? null : result.Value,
                result.Variant,
                result.Reason.ToWireString(),
                result.ErrorCode.ToWireString(),
                result.RuleId);
        }).ToList();
        return Results.Ok(new PlaygroundResponse(cached.Version, results));
    }
}
