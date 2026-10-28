namespace Bunting.Sdk;

/// <summary>Configuration of <see cref="BuntingProvider"/>.</summary>
public sealed class BuntingOptions
{
    /// <summary>Base address of the Bunting server, e.g. <c>https://flags.example.com</c>. Required.</summary>
    public Uri? ServerUrl { get; set; }

    /// <summary>A server API key (<c>bnt_srv_...</c>). Required.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Optional path of a snapshot file. The last downloaded ruleset is written there and read on start-up,
    /// so an application can boot with the last known flags while the server is unreachable.
    /// </summary>
    public string? SnapshotPath { get; set; }

    /// <summary>How long initialization waits for the first ruleset before it completes without one.</summary>
    public TimeSpan InitializeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Polling interval while the stream is not connected.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>First delay before a stream reconnect; doubles up to <see cref="MaxReconnectDelay"/> (with jitter).</summary>
    public TimeSpan MinReconnectDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A stream that stays silent for this long (the server sends a heartbeat every 25 s) is considered dead.</summary>
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(70);

    /// <summary>Timeout of a single ruleset request.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The provider reports <c>Stale</c> when the ruleset could not be confirmed with the server for this long.
    /// Evaluation keeps working with the last known ruleset.
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Optional pre-configured client (tests, custom handlers). The provider does not dispose it.</summary>
    public HttpClient? HttpClient { get; set; }

    internal void Validate()
    {
        if (ServerUrl is null || !ServerUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("BuntingOptions.ServerUrl must be an absolute URI.", nameof(BuntingOptions));
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ArgumentException("BuntingOptions.ApiKey is required.", nameof(BuntingOptions));
        }
    }
}
