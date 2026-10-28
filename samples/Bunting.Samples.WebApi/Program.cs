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

app.MapGet("/", async (IFeatureClient flags, string? user) =>
{
    var context = EvaluationContext.Builder()
        .SetTargetingKey(user ?? "anonymous")
        .Build();

    var newGreeting = await flags.GetBooleanValueAsync("new-greeting", false, context);
    return newGreeting ? $"Welcome back, {user ?? "friend"}! (new greeting)" : "Hello.";
});

app.MapGet("/details", async (IFeatureClient flags, string? user) =>
{
    var context = EvaluationContext.Builder().SetTargetingKey(user ?? "anonymous").Build();
    var details = await flags.GetBooleanDetailsAsync("new-greeting", false, context);
    return Results.Ok(new { details.Value, details.Variant, details.Reason, error = details.ErrorType.ToString() });
});

app.Run();
