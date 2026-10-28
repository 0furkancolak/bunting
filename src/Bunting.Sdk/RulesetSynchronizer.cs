using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bunting.Evaluation;
using Microsoft.Extensions.Logging;

namespace Bunting.Sdk;

internal enum SyncSignal
{
    /// <summary>The ruleset was confirmed with the server (first time, or after being stale).</summary>
    Ready,

    /// <summary>A newer ruleset was installed.</summary>
    ConfigurationChanged,

    /// <summary>The ruleset could not be confirmed for too long; evaluation continues with the last known one.</summary>
    Stale,

    /// <summary>No ruleset is available and the server cannot be used.</summary>
    Error,
}

/// <summary>
/// Keeps the in-memory ruleset current: SSE for changes, ETag requests to fetch, polling while the stream is down.
/// The compiled ruleset is replaced by a single reference swap, so evaluation never takes a lock.
/// </summary>
internal sealed partial class RulesetSynchronizer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<RulesetSynchronizer, byte> Active = [];

    private readonly BuntingOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly Action<SyncSignal, string?> _signal;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _firstConfirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CompiledRuleset? _ruleset;
    private string? _etag;
    private long _lastConfirmedTicks;
    private int _state = (int)ConnectionState.Disconnected;
    private int _stale;
    private int _failing;
    private int _disposed;
    private Task[] _loops = [];

    static RulesetSynchronizer()
    {
        BuntingTelemetry.Meter.CreateObservableGauge(
            "bunting.sdk.ruleset.age",
            () => Active.Keys.Select(s => new Measurement<double>(s.Age?.TotalSeconds ?? -1)).ToArray(),
            unit: "s",
            description: "Seconds since the ruleset was last confirmed with the server (-1 if never)");
        BuntingTelemetry.Meter.CreateObservableGauge(
            "bunting.sdk.connection.state",
            () => Active.Keys.Select(s => new Measurement<int>((int)s.State)).ToArray(),
            description: "0 = disconnected, 1 = polling, 2 = streaming");
    }

    public RulesetSynchronizer(BuntingOptions options, HttpClient http, ILogger logger, Action<SyncSignal, string?> signal)
    {
        _options = options;
        _http = http;
        _logger = logger;
        _signal = signal;
    }

    /// <summary>The current ruleset, or <see langword="null"/> when none was ever loaded.</summary>
    public CompiledRuleset? Ruleset => Volatile.Read(ref _ruleset);

    public ConnectionState State => (ConnectionState)Volatile.Read(ref _state);

    public bool IsStale => Volatile.Read(ref _stale) == 1;

    public TimeSpan? Age => Volatile.Read(ref _lastConfirmedTicks) is var ticks and > 0
        ? DateTimeOffset.UtcNow - new DateTimeOffset(ticks, TimeSpan.Zero)
        : null;

    public void Start()
    {
        Active[this] = 0;
        LoadSnapshot();
        var ct = _stopping.Token;
        _loops =
        [
            Task.Run(() => InitialSyncAsync(ct), ct),
            Task.Run(() => StreamLoopAsync(ct), ct),
            Task.Run(() => PollLoopAsync(ct), ct),
        ];
    }

    /// <summary>Completes <see langword="true"/> when the server confirmed a ruleset, <see langword="false"/> on timeout.</summary>
    public async Task<bool> WaitForConfirmationAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _firstConfirmation.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Active.TryRemove(this, out _);
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        _stopping.Dispose();
        _refreshLock.Dispose();
    }

    private void LoadSnapshot()
    {
        if (_options.SnapshotPath is not { Length: > 0 } path || SnapshotStore.TryLoad(path, Json, _logger) is not { } snapshot)
        {
            return;
        }

        var compiled = CompiledRuleset.Compile(snapshot);
        Volatile.Write(ref _ruleset, compiled);
        _etag = $"\"{compiled.Version}\"";
        LogSnapshotLoaded(_logger, compiled.Version, path);
    }

    private async Task InitialSyncAsync(CancellationToken ct)
    {
        try
        {
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.PollingInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (State != ConnectionState.Streaming)
                {
                    await RefreshAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task StreamLoopAsync(CancellationToken ct)
    {
        var delay = _options.MinReconnectDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/sdk/stream");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                using var connecting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connecting.CancelAfter(_options.RequestTimeout);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connecting.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                SetState(ConnectionState.Streaming);
                LogStreamConnected(_logger);

                await foreach (var message in SseReader.ReadAsync(stream, _options.StreamIdleTimeout, ct).ConfigureAwait(false))
                {
                    delay = _options.MinReconnectDelay;
                    if (message.Name == "ruleset-updated")
                    {
                        await OnVersionAnnouncedAsync(message.Data, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogStreamFailed(_logger, ex.Message);
            }

            SetState(ConnectionState.Polling);
            try
            {
                await Task.Delay(WithJitter(delay), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxReconnectDelay.Ticks));
        }
    }

    private async Task OnVersionAnnouncedAsync(string data, CancellationToken ct)
    {
        long announced;
        try
        {
            using var document = JsonDocument.Parse(data);
            announced = document.RootElement.GetProperty("version").GetInt64();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return; // an event we do not understand is ignored
        }

        if (Ruleset is { } current && current.Version == announced)
        {
            Confirm();
            return;
        }

        // A failed download drops the stream so that the reconnect (with backoff) retries it.
        if (!await RefreshAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The announced ruleset could not be downloaded.");
        }
    }

    /// <summary>Downloads the ruleset if it changed. Never throws except on cancellation.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var activity = BuntingTelemetry.ActivitySource.StartActivity("bunting.sdk.sync");
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/sdk/ruleset");
            if (_etag is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.RequestTimeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                BuntingTelemetry.Syncs.Add(1, new KeyValuePair<string, object?>("outcome", "not_modified"));
                Confirm();
                return true;
            }

            if (!response.IsSuccessStatusCode)
            {
                Fail($"The server answered {(int)response.StatusCode}");
                return false;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            var snapshot = JsonSerializer.Deserialize<RulesetSnapshot>(bytes, Json) ?? throw new JsonException("Empty ruleset");
            var compiled = CompiledRuleset.Compile(snapshot);
            await InstallAsync(compiled, response.Headers.ETag?.ToString(), bytes).ConfigureAwait(false);
            BuntingTelemetry.Syncs.Add(1, new KeyValuePair<string, object?>("outcome", "updated"));
            activity?.SetTag("bunting.ruleset.version", compiled.Version);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task InstallAsync(CompiledRuleset compiled, string? etag, byte[] raw)
    {
        var previous = Ruleset;
        if (previous is not null && compiled.Version < previous.Version)
        {
            Confirm(); // never move back to an older ruleset
            return;
        }

        Volatile.Write(ref _ruleset, compiled);
        _etag = etag ?? $"\"{compiled.Version}\"";
        var wasReady = Confirm();

        if (_options.SnapshotPath is { Length: > 0 } path)
        {
            await SnapshotStore.TrySaveAsync(path, raw, _logger).ConfigureAwait(false);
        }

        LogInstalled(_logger, compiled.Version);
        if (wasReady && previous is not null && previous.Version != compiled.Version)
        {
            _signal(SyncSignal.ConfigurationChanged, null);
        }
    }

    /// <summary>Marks the ruleset as confirmed. Returns whether a <see cref="SyncSignal.Ready"/> was not needed (already ready).</summary>
    private bool Confirm()
    {
        Volatile.Write(ref _lastConfirmedTicks, DateTimeOffset.UtcNow.UtcTicks);
        Interlocked.Exchange(ref _failing, 0);
        if (State == ConnectionState.Disconnected)
        {
            SetState(ConnectionState.Polling);
        }

        var first = _firstConfirmation.TrySetResult();
        var recovered = Interlocked.Exchange(ref _stale, 0) == 1;
        if (first || recovered)
        {
            _signal(SyncSignal.Ready, null);
            return false;
        }

        return true;
    }

    private void Fail(string reason)
    {
        BuntingTelemetry.Syncs.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
        LogSyncFailed(_logger, reason);
        var first = Interlocked.Exchange(ref _failing, 1) == 0;
        if (Ruleset is null)
        {
            if (first)
            {
                _signal(SyncSignal.Error, reason);
            }

            return;
        }

        // Stale when the ruleset was never confirmed in this process (snapshot) or the last confirmation is old.
        var age = Age;
        if ((age is null || age >= _options.StaleAfter) && Interlocked.Exchange(ref _stale, 1) == 0)
        {
            _signal(SyncSignal.Stale, reason);
        }
    }

    private void SetState(ConnectionState state) => Volatile.Write(ref _state, (int)state);

    private static TimeSpan WithJitter(TimeSpan delay) => delay * (0.5 + Random.Shared.NextDouble() * 0.5);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded Bunting ruleset version {Version} from snapshot {Path}")]
    private static partial void LogSnapshotLoaded(ILogger logger, long version, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installed Bunting ruleset version {Version}")]
    private static partial void LogInstalled(ILogger logger, long version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to the Bunting stream")]
    private static partial void LogStreamConnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Bunting stream ended: {Reason}")]
    private static partial void LogStreamFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not synchronize the Bunting ruleset: {Reason}")]
    private static partial void LogSyncFailed(ILogger logger, string reason);
}
