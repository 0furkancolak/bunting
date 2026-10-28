using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;

namespace Bunting.Server.Features.Realtime;

internal static class RealtimeTelemetry
{
    public static readonly Meter Meter = new("Bunting.Server");
    public static readonly UpDownCounter<int> Connections = Meter.CreateUpDownCounter<int>("bunting.sse.connections", description: "Open SSE connections");
    public static readonly Counter<long> Notifications = Meter.CreateCounter<long>("bunting.ruleset.notifications", description: "NOTIFY messages received");
    public static readonly Counter<long> Reconnects = Meter.CreateCounter<long>("bunting.listener.reconnects", description: "LISTEN connection (re)establishments");
}

/// <summary>
/// Fans ruleset version changes out to the SSE connections of this instance. Each subscription holds at most one pending
/// version (newer versions replace older ones), so a slow client never blocks publishers or other clients.
/// </summary>
public sealed class ChangeHub
{
    private sealed class EnvironmentState
    {
        public readonly object Gate = new();
        public readonly Dictionary<long, Subscription> Subscribers = [];
        public long LastVersion;
    }

    private readonly ConcurrentDictionary<Guid, EnvironmentState> _environments = new();
    private long _nextId;

    /// <summary>Environments with at least one open subscription.</summary>
    public IReadOnlyCollection<Guid> ActiveEnvironments =>
        [.. _environments.Where(kv => { lock (kv.Value.Gate) { return kv.Value.Subscribers.Count > 0; } }).Select(kv => kv.Key)];

    public int SubscriberCount(Guid environmentId)
    {
        if (!_environments.TryGetValue(environmentId, out var state))
        {
            return 0;
        }

        lock (state.Gate)
        {
            return state.Subscribers.Count;
        }
    }

    /// <summary>
    /// Subscribes to changes after <paramref name="knownVersion"/> (the version the caller already told its client about).
    /// If a newer version was published in the meantime it is queued immediately.
    /// </summary>
    public Subscription Subscribe(Guid environmentId, long knownVersion)
    {
        var state = _environments.GetOrAdd(environmentId, _ => new EnvironmentState());
        var subscription = new Subscription(this, environmentId, Interlocked.Increment(ref _nextId));
        lock (state.Gate)
        {
            state.Subscribers[subscription.Id] = subscription;
            state.LastVersion = Math.Max(state.LastVersion, knownVersion);
            if (state.LastVersion > knownVersion)
            {
                subscription.Enqueue(state.LastVersion);
            }
        }

        RealtimeTelemetry.Connections.Add(1);
        return subscription;
    }

    /// <summary>Announces <paramref name="version"/>; versions that are not newer than the last published one are ignored.</summary>
    public void Publish(Guid environmentId, long version)
    {
        var state = _environments.GetOrAdd(environmentId, _ => new EnvironmentState());
        lock (state.Gate)
        {
            if (version <= state.LastVersion)
            {
                return;
            }

            state.LastVersion = version;
            foreach (var subscriber in state.Subscribers.Values)
            {
                subscriber.Enqueue(version);
            }
        }
    }

    internal void Remove(Guid environmentId, long id)
    {
        if (_environments.TryGetValue(environmentId, out var state))
        {
            lock (state.Gate)
            {
                state.Subscribers.Remove(id);
            }
        }

        RealtimeTelemetry.Connections.Add(-1);
    }

    public sealed class Subscription : IDisposable
    {
        private readonly ChangeHub _hub;
        private readonly Guid _environmentId;
        private readonly Channel<long> _channel = Channel.CreateBounded<long>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private int _disposed;

        internal Subscription(ChangeHub hub, Guid environmentId, long id)
        {
            _hub = hub;
            _environmentId = environmentId;
            Id = id;
        }

        internal long Id { get; }

        public ChannelReader<long> Versions => _channel.Reader;

        internal void Enqueue(long version) => _channel.Writer.TryWrite(version);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _channel.Writer.TryComplete();
                _hub.Remove(_environmentId, Id);
            }
        }
    }
}
