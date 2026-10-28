using System.Text;
using Bunting.Server.Features.Rulesets;
using Bunting.Server.Infrastructure;
using Microsoft.Extensions.Options;

namespace Bunting.Server.Features.Realtime;

public sealed class StreamOptions
{
    /// <summary>Seconds between <c>heartbeat</c> events on idle streams.</summary>
    public double HeartbeatSeconds { get; set; } = 25;
}

/// <summary>
/// Ruleset download and change streams for SDKs (server keys) and browsers/mobile apps (client keys).
/// Streams only carry version numbers: clients re-fetch the ruleset (servers) or re-run the OFREP bulk call (clients).
/// </summary>
public static class RealtimeEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var sdk = api.MapGroup("/sdk").RequireAuthorization(ApiKeyPolicies.ServerKey).RequireRateLimiting(ApiKeyPolicies.RateLimit);
        sdk.MapGet("/ruleset", GetRuleset);
        sdk.MapGet("/stream", (HttpContext http, RulesetCache cache, ChangeHub hub, IOptions<StreamOptions> options, CancellationToken ct) =>
            Stream(http, cache, hub, options.Value, "ruleset-updated", ct));

        api.MapGet("/client/stream", (HttpContext http, RulesetCache cache, ChangeHub hub, IOptions<StreamOptions> options, CancellationToken ct) =>
                Stream(http, cache, hub, options.Value, "flags-changed", ct))
            .RequireAuthorization(ApiKeyPolicies.AnyKey)
            .RequireRateLimiting(ApiKeyPolicies.RateLimit);
    }

    private static Guid EnvironmentId(HttpContext http) => Guid.Parse(http.User.FindFirst(ApiKeyPolicies.EnvironmentClaim)!.Value);

    private static async Task<IResult> GetRuleset(HttpContext http, RulesetCache cache, CancellationToken ct)
    {
        var cached = await cache.GetAsync(EnvironmentId(http), ct);
        var etag = ETags.Format(cached.Version);
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "private, no-cache";
        if (http.Request.Headers.IfNoneMatch.ToString().Split(',', StringSplitOptions.TrimEntries).Any(v => v.Trim('"') == cached.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Bytes(cached.SnapshotJson, "application/json");
    }

    private static async Task Stream(
        HttpContext http, RulesetCache cache, ChangeHub hub, StreamOptions options, string eventName, CancellationToken ct)
    {
        var environmentId = EnvironmentId(http);
        var current = await cache.GetAsync(environmentId, ct);

        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Append("X-Accel-Buffering", "no"); // keep reverse proxies from buffering the stream

        using var subscription = hub.Subscribe(environmentId, current.Version);
        await WriteAsync(response, eventName, $"{{\"version\":{current.Version}}}", ct);

        var heartbeat = TimeSpan.FromSeconds(options.HeartbeatSeconds);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(heartbeat);
                try
                {
                    var version = await subscription.Versions.ReadAsync(wait.Token);
                    await WriteAsync(response, eventName, $"{{\"version\":{version}}}", ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await WriteAsync(response, "heartbeat", "{}", ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected or server stopping
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            // subscription disposed
        }
    }

    private static async Task WriteAsync(HttpResponse response, string eventName, string data, CancellationToken ct)
    {
        await response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", Encoding.UTF8, ct);
        await response.Body.FlushAsync(ct);
    }
}
