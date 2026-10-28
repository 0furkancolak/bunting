using Bunting.Domain;
using Bunting.Server.Data;
using Bunting.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Bunting.Server.Features.ApiKeys;

public sealed record CreateApiKeyRequest(string Name, ApiKeyType Type);

public sealed record ApiKeyDto(Guid Id, string Name, ApiKeyType Type, string Hint, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

/// <summary>Returned once, at creation: the only time the plaintext key is available.</summary>
public sealed record CreatedApiKeyDto(Guid Id, string Name, ApiKeyType Type, string Key, string Hint, DateTimeOffset CreatedAt);

public static class ApiKeyEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{projectKey}/environments/{environmentKey}/api-keys");
        g.MapGet("/", List).RequireAuthorization(ManagementPolicies.Owner);
        g.MapPost("/", Create).RequireAuthorization(ManagementPolicies.Owner);
        g.MapDelete("/{keyId:guid}", Revoke).RequireAuthorization(ManagementPolicies.Owner);
    }

    private static ApiKeyDto ToDto(ApiKey k) => new(k.Id, k.Name, k.Type, k.Hint, k.CreatedAt, k.RevokedAt);

    private static async Task<IResult> List(string projectKey, string environmentKey, BuntingDbContext db, CancellationToken ct)
    {
        try
        {
            var project = await db.RequireProjectAsync(projectKey, ct);
            var env = await db.RequireEnvironmentAsync(project.Id, environmentKey, ct);
            var keys = await db.ApiKeys.AsNoTracking().Where(k => k.EnvironmentId == env.Id).OrderBy(k => k.CreatedAt).ToListAsync(ct);
            return Results.Ok(keys.Select(ToDto));
        }
        catch (ApiException e)
        {
            return e.Result;
        }
    }

    private static Task<IResult> Create(
        string projectKey, string environmentKey, CreateApiKeyRequest request, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
                {
                    throw Problems.Throw(Problems.Validation("name", "Name is required and at most 200 characters."));
                }

                if (!Enum.IsDefined(request.Type))
                {
                    throw Problems.Throw(Problems.Validation("type", "Type must be Server or Client."));
                }

                var project = await db.RequireProjectAsync(projectKey, ct);
                var env = await db.RequireEnvironmentAsync(project.Id, environmentKey, ct);
                var (plaintext, hash, hint) = ApiKeyService.Generate(request.Type);
                var key = new ApiKey
                {
                    Id = Guid.CreateVersion7(),
                    EnvironmentId = env.Id,
                    Type = request.Type,
                    Name = request.Name.Trim(),
                    KeyHash = hash,
                    Hint = hint,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                db.ApiKeys.Add(key);

                // The audit log never contains the key itself.
                log.Audit(project.Id, env.Id, "api-key", key.Id.ToString(), "created", null, ToDto(key));
                await Task.CompletedTask;
                return Results.Created(
                    $"/api/v1/projects/{project.Key}/environments/{env.Key}/api-keys/{key.Id}",
                    new CreatedApiKeyDto(key.Id, key.Name, key.Type, plaintext, key.Hint, key.CreatedAt));
            },
            ct);

    private static Task<IResult> Revoke(
        string projectKey, string environmentKey, Guid keyId, BuntingDbContext db, ApiKeyService keys, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                var project = await db.RequireProjectAsync(projectKey, ct);
                var env = await db.RequireEnvironmentAsync(project.Id, environmentKey, ct);
                var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == keyId && k.EnvironmentId == env.Id, ct)
                    ?? throw Problems.Throw(Problems.NotFound($"API key '{keyId}' was not found."));
                if (key.RevokedAt is null)
                {
                    var before = ToDto(key);
                    key.RevokedAt = DateTimeOffset.UtcNow;
                    log.Audit(project.Id, env.Id, "api-key", key.Id.ToString(), "revoked", before, ToDto(key));
                    log.AfterCommit(() => keys.Forget(key.KeyHash));
                }

                return Results.NoContent();
            },
            ct);
}
