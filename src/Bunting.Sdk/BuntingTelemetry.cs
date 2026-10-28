using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Bunting.Sdk;

/// <summary>Names of the telemetry sources of the SDK. Evaluation contexts are never recorded.</summary>
public static class BuntingTelemetry
{
    public const string SourceName = "Bunting.Sdk";

    internal static readonly ActivitySource ActivitySource = new(SourceName);

    internal static readonly Meter Meter = new(SourceName);

    internal static readonly Counter<long> Evaluations =
        Meter.CreateCounter<long>("bunting.sdk.evaluations", description: "Number of flag evaluations");

    internal static readonly Counter<long> Syncs =
        Meter.CreateCounter<long>("bunting.sdk.syncs", description: "Ruleset downloads by outcome");
}

/// <summary>State of the connection to the server.</summary>
public enum ConnectionState
{
    /// <summary>Neither streaming nor reachable.</summary>
    Disconnected = 0,

    /// <summary>The stream is down; the ruleset is polled.</summary>
    Polling = 1,

    /// <summary>Connected to the SSE stream.</summary>
    Streaming = 2,
}
