# Bunting.Sdk

OpenFeature provider for [Bunting](https://github.com/furkancolak/bunting). Flags are evaluated locally from an in-memory
ruleset that is kept up to date over Server-Sent Events, with polling as a fallback.

```csharp
builder.Services.AddOpenFeature(o => o.AddBuntingProvider(opts =>
{
    opts.ServerUrl = new Uri("https://flags.example.com");
    opts.ApiKey = "bnt_srv_...";
    opts.SnapshotPath = "bunting-snapshot.json"; // optional
}));
```

Evaluation never throws: on any problem you get your default value and a reason. See the repository for details.
