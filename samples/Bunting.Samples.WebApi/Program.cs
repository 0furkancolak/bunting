using Bunting.Sdk;
using OpenFeature;
using OpenFeature.Model;

var builder = WebApplication.CreateBuilder(args);

// Flags are evaluated in memory; the ruleset is kept current over SSE. Without a reachable server the app
// starts from the snapshot file (if any) or serves the defaults below.
builder.Services.AddOpenFeature(feature => feature.AddBuntingProvider(options =>
{
    options.ServerUrl = new Uri(builder.Configuration["Bunting:ServerUrl"] ?? "http://localhost:5080");
    options.ApiKey = builder.Configuration["Bunting:ApiKey"] ?? "bnt_srv_replace_me";
    options.SnapshotPath = builder.Configuration["Bunting:SnapshotPath"] ?? "bunting-snapshot.json";
}));

var app = builder.Build();

app.MapGet("/", async (IFeatureClient flags, string? user, string? plan) =>
{
    var context = EvaluationContext.Builder()
        .SetTargetingKey(user ?? "anonymous")
        .Set("plan", plan ?? "free")
        .Build();

    var newCheckout = await flags.GetBooleanValueAsync("new-checkout", false, context);
    var banner = await flags.GetStringValueAsync("banner-text", "Hello", context);
    return $"{banner} — {(newCheckout ? "new checkout" : "classic checkout")} for {user ?? "anonymous"}";
});

app.MapGet("/details", async (IFeatureClient flags, string? user, string? plan) =>
{
    var context = EvaluationContext.Builder().SetTargetingKey(user ?? "anonymous").Set("plan", plan ?? "free").Build();
    var details = await flags.GetBooleanDetailsAsync("new-checkout", false, context);
    return Results.Ok(new { details.Value, details.Variant, details.Reason, error = details.ErrorType.ToString() });
});

app.Run();
