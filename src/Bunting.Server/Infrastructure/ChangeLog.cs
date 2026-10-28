using Bunting.Domain;
using Bunting.Server.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bunting.Server.Infrastructure;

/// <summary>
/// The single write path of the Management API. A write scope runs in one transaction that contains the change,
/// its <see cref="AuditEntry"/>, the environment ruleset version bump(s) and the
/// <c>NOTIFY bunting_ruleset_changed</c> (delivered by Postgres only if the transaction commits).
/// </summary>
public sealed class ChangeLog(BuntingDbContext db, IHttpContextAccessor http, Features.Rulesets.RulesetCache rulesets)
{
    public const string NotifyChannel = "bunting_ruleset_changed";

    private readonly List<Action> _afterCommit = [];

    public string Actor => http.HttpContext?.User.Identity?.Name ?? "system";

    /// <summary>Runs <paramref name="body"/> transactionally. <see cref="ApiException"/> and conflicts roll back and become problems.</summary>
    public async Task<IResult> WriteAsync(Func<Task<IResult>> body, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await body();
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            RunAfterCommit();
            return result;
        }
        catch (ApiException e)
        {
            await tx.RollbackAsync(ct);
            return e.Result;
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            return Problems.Conflict("The resource was modified or deleted concurrently. Fetch the latest version and retry.");
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync(ct);
            return Problems.Conflict("A resource with the same key already exists.");
        }
    }

    /// <summary>Registers in-process work (cache invalidation) to run only after the transaction committed.</summary>
    public void AfterCommit(Action action) => _afterCommit.Add(action);

    private void RunAfterCommit()
    {
        foreach (var action in _afterCommit)
        {
            action();
        }

        _afterCommit.Clear();
    }

    public void Audit(Guid projectId, Guid? environmentId, string entityType, string entityId, string action, object? before, object? after) =>
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            EnvironmentId = environmentId,
            Actor = Actor,
            Timestamp = DateTimeOffset.UtcNow,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            BeforeJson = before is null ? null : BuntingJson.Serialize(before),
            AfterJson = after is null ? null : BuntingJson.Serialize(after),
        });

    /// <summary>Atomically increments the environment's ruleset version and queues the NOTIFY. Returns the new version.</summary>
    public async Task<long> BumpEnvironmentAsync(Guid environmentId, CancellationToken ct)
    {
        // The UPDATE takes a row lock that serialises concurrent writers of the same environment until commit.
        // UPDATE ... RETURNING is non-composable SQL, so it must be materialised with ToListAsync.
        var version = (await db.Database
            .SqlQuery<long>($"UPDATE environments SET \"RulesetVersion\" = \"RulesetVersion\" + 1 WHERE \"Id\" = {environmentId} RETURNING \"RulesetVersion\" AS \"Value\"")
            .ToListAsync(ct)).Single();
        AfterCommit(() => rulesets.Invalidate(environmentId));
        var payload = $"{environmentId}:{version}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_notify({NotifyChannel}, {payload})", ct);
        return version;
    }

    public async Task BumpProjectAsync(Guid projectId, CancellationToken ct)
    {
        var ids = await db.Environments.Where(e => e.ProjectId == projectId).OrderBy(e => e.Id).Select(e => e.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            await BumpEnvironmentAsync(id, ct);
        }
    }
}
