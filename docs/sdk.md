# .NET SDK

```csharp
builder.Services.AddOpenFeature(o => o.AddBuntingProvider(opts =>
{
    opts.ServerUrl = new Uri("https://flags.example.com");
    opts.ApiKey = "bnt_srv_...";          // a server key
    opts.SnapshotPath = "bunting.json";   // optional
}));
```

Then use the standard OpenFeature client (`IFeatureClient`). See `samples/Bunting.Samples.WebApi`.

## Behaviour

| Situation | Result |
|---|---|
| Server reachable | Evaluation in memory; changes arrive over SSE (typically milliseconds) |
| Stream drops | Reconnect with exponential backoff + jitter; poll every 30 s meanwhile |
| Server unreachable | Last ruleset keeps serving; `Stale` event after 2 min, reason `STALE` |
| Server back | `Ready` event; newer ruleset installed; `ConfigurationChanged` |
| Start without server, snapshot present | Boots from the snapshot, `Stale` until confirmed |
| Start without server, no snapshot | Defaults with `PROVIDER_NOT_READY`; `Ready` when the server appears |
| Wrong API key | `Error` event, defaults with `PROVIDER_NOT_READY` |
| Unknown flag / wrong type | Default with `FLAG_NOT_FOUND` / `TYPE_MISMATCH` |

## Options

`ServerUrl`, `ApiKey`, `SnapshotPath`, `InitializeTimeout` (5 s), `PollingInterval` (30 s), `MinReconnectDelay` (0.5 s), `MaxReconnectDelay` (30 s), `StreamIdleTimeout` (70 s), `RequestTimeout` (10 s), `StaleAfter` (2 min), `HttpClient`.

## Telemetry

`ActivitySource` and `Meter` named `Bunting.Sdk`: `bunting.sdk.evaluations` (tags `feature_flag.key`, `feature_flag.result.reason`), `bunting.sdk.syncs` (`outcome`), `bunting.sdk.ruleset.age` (s), `bunting.sdk.connection.state` (0 disconnected, 1 polling, 2 streaming). Evaluation contexts are never recorded.
