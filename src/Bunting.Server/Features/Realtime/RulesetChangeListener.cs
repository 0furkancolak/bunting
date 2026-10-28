using Bunting.Server.Data;
using Bunting.Server.Features.Rulesets;
using Bunting.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bunting.Server.Features.Realtime;

/// <summary>
/// Listens on <c>bunting_ruleset_changed</c> so that changes made through any instance reach this instance's cache and
/// SSE clients. On every (re)connect, and periodically, the versions in the database are compared with what this instance
/// knows, so notifications missed while disconnected are never lost.
/// </summary>
public sealed partial class RulesetChangeListener(
    NpgsqlDataSource dataSource,
    RulesetCache cache,
    ChangeHub hub,
    IServiceScopeFactory scopes,
    ILogger<RulesetChangeListener> logger) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ruleset listener disconnected; retrying in {Delay}.")]
    private partial void LogDisconnected(Exception exception, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening for ruleset changes.")]
    private partial void LogListening();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromMilliseconds(250);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken);
                backoff = TimeSpan.FromMilliseconds(250);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                LogDisconnected(e, backoff);
            }

            try
            {
                await Task.Delay(backoff + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        connection.Notification += (_, e) => OnNotification(e.Payload);
        await using (var listen = new NpgsqlCommand($"LISTEN {ChangeLog.NotifyChannel}", connection))
        {
            await listen.ExecuteNonQueryAsync(ct);
        }

        RealtimeTelemetry.Reconnects.Add(1);
        LogListening();

        // LISTEN is active, so anything committed from now on arrives as a notification; reconcile what happened before.
        await ReconcileAsync(ct);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reconciler = ReconcilePeriodicallyAsync(linked.Token);
        try
        {
            while (true)
            {
                await connection.WaitAsync(ct);
            }
        }
        finally
        {
            await linked.CancelAsync();
            await reconciler;
        }
    }

    private void OnNotification(string payload)
    {
        // Payload: "<environmentId>:<version>". Anything else is ignored.
        var parts = payload.Split(':');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var environmentId) || !long.TryParse(parts[1], out var version))
        {
            return;
        }

        RealtimeTelemetry.Notifications.Add(1);
        Apply(environmentId, version);
    }

    /// <summary>Order matters: the cache must be invalid before clients are told to re-fetch.</summary>
    private void Apply(Guid environmentId, long version)
    {
        cache.InvalidateIfOlderThan(environmentId, version);
        hub.Publish(environmentId, version);
    }

    private async Task ReconcilePeriodicallyAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ReconcileInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await ReconcileAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // listener connection ended
        }
    }

    /// <summary>Compares database versions with this instance's view for every environment it serves.</summary>
    internal async Task ReconcileAsync(CancellationToken ct)
    {
        var ids = hub.ActiveEnvironments.Concat(cache.CachedEnvironmentIds).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BuntingDbContext>();
        var versions = await db.Environments.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, e.RulesetVersion })
            .ToListAsync(ct);
        foreach (var environment in versions)
        {
            Apply(environment.Id, environment.RulesetVersion);
        }
    }
}
