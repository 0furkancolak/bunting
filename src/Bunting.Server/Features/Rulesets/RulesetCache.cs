using System.Collections.Concurrent;
using Bunting.Evaluation;
using Bunting.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Bunting.Server.Features.Rulesets;

/// <summary>A compiled ruleset together with the version it was built from.</summary>
public sealed record CachedRuleset(Guid EnvironmentId, long Version, CompiledRuleset Ruleset, byte[] SnapshotJson, DateTimeOffset LoadedAt);

/// <summary>
/// In-memory cache of compiled rulesets per environment. Entries are invalidated right after local commits;
/// a periodic version check is the safety net (cross-instance push via LISTEN/NOTIFY arrives in the real-time phase).
/// </summary>
public sealed class RulesetCache(IServiceScopeFactory scopes, TimeProvider time)
{
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, CachedRuleset> _entries = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async ValueTask<CachedRuleset> GetAsync(Guid environmentId, CancellationToken ct)
    {
        if (_entries.TryGetValue(environmentId, out var entry) && time.GetUtcNow() - entry.LoadedAt < RecheckInterval)
        {
            return entry;
        }

        var gate = _locks.GetOrAdd(environmentId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_entries.TryGetValue(environmentId, out entry) && time.GetUtcNow() - entry.LoadedAt < RecheckInterval)
            {
                return entry;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BuntingDbContext>();
            var current = await db.Environments.AsNoTracking().Where(e => e.Id == environmentId).Select(e => e.RulesetVersion).SingleAsync(ct);
            if (entry is not null && entry.Version == current)
            {
                entry = entry with { LoadedAt = time.GetUtcNow() };
            }
            else
            {
                var snapshot = await scope.ServiceProvider.GetRequiredService<RulesetSnapshotBuilder>().BuildAsync(environmentId, ct);
                entry = new CachedRuleset(
                    environmentId,
                    snapshot.Version,
                    CompiledRuleset.Compile(snapshot),
                    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot, Infrastructure.BuntingJson.Options),
                    time.GetUtcNow());
            }

            _entries[environmentId] = entry;
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }

    public IReadOnlyCollection<Guid> CachedEnvironmentIds => [.. _entries.Keys];

    public void Invalidate(Guid environmentId) => _entries.TryRemove(environmentId, out _);

    /// <summary>Drops the entry unless it already reflects <paramref name="version"/> or newer.</summary>
    public void InvalidateIfOlderThan(Guid environmentId, long version)
    {
        if (_entries.TryGetValue(environmentId, out var entry) && entry.Version < version)
        {
            _entries.TryRemove(new KeyValuePair<Guid, CachedRuleset>(environmentId, entry));
        }
    }
}
