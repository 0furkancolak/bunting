using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Bunting.Domain;
using Bunting.Evaluation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bunting.Sdk.Tests;

/// <summary>
/// An in-process stand-in for the Bunting server's SDK endpoints. It can be stopped and restarted on the same port
/// and counts the requests it served, which lets tests simulate outages precisely.
/// </summary>
public sealed class FakeBuntingServer : IAsyncDisposable
{
    public const string ApiKey = "bnt_srv_test_key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly List<Channel<long>> _streams = [];
    private WebApplication? _app;
    private RulesetSnapshot _snapshot = new(1, [], []);

    public FakeBuntingServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
    }

    public int Port { get; }

    /// <summary>When false the stream endpoint answers 503, forcing clients to poll.</summary>
    public bool StreamEnabled { get; set; } = true;

    public Uri Url => new($"http://127.0.0.1:{Port}");

    public int RulesetRequests { get; private set; }

    public int NotModifiedResponses { get; private set; }

    public int StreamConnections { get; private set; }

    public long Version => _snapshot.Version;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, Port));
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromMilliseconds(100));
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (context.Request.Headers["X-API-Key"] != ApiKey)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });

        app.MapGet("/api/v1/sdk/ruleset", (HttpContext context) =>
        {
            RulesetSnapshot snapshot;
            lock (_gate)
            {
                RulesetRequests++;
                snapshot = _snapshot;
            }

            var etag = $"\"{snapshot.Version}\"";
            if (context.Request.Headers.IfNoneMatch == etag)
            {
                lock (_gate)
                {
                    NotModifiedResponses++;
                }

                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            context.Response.Headers.ETag = etag;
            return Results.Bytes(JsonSerializer.SerializeToUtf8Bytes(snapshot, Json), "application/json");
        });

        app.MapGet("/api/v1/sdk/stream", async (HttpContext context) =>
        {
            if (!StreamEnabled)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            var channel = Channel.CreateUnbounded<long>();
            long initial;
            lock (_gate)
            {
                StreamConnections++;
                _streams.Add(channel);
                initial = _snapshot.Version;
            }

            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync($"event: ruleset-updated\ndata: {{\"version\":{initial}}}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            try
            {
                await foreach (var version in channel.Reader.ReadAllAsync(context.RequestAborted))
                {
                    await context.Response.WriteAsync($"event: ruleset-updated\ndata: {{\"version\":{version}}}\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
                // client went away or the server is stopping
            }
            finally
            {
                lock (_gate)
                {
                    _streams.Remove(channel);
                }
            }
        });

        await app.StartAsync();
        _app = app;
    }

    public async Task StopAsync()
    {
        if (_app is { } app)
        {
            _app = null;
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>Replaces the ruleset (version is incremented) and announces it on all open streams.</summary>
    public long Publish(params FlagSnapshot[] flags) => Publish(flags, []);

    public long Publish(FlagSnapshot[] flags, SegmentSnapshot[] segments, bool announce = true)
    {
        lock (_gate)
        {
            _snapshot = new RulesetSnapshot(_snapshot.Version + 1, flags, segments);
            if (announce)
            {
                foreach (var stream in _streams)
                {
                    stream.Writer.TryWrite(_snapshot.Version);
                }
            }

            return _snapshot.Version;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

internal static class Flags
{
    public static FlagSnapshot Bool(string key, bool on, params Rule[] rules) => new(
        key,
        FlagType.Boolean,
        new Dictionary<string, JsonElement> { ["on"] = JsonSerializer.SerializeToElement(true), ["off"] = JsonSerializer.SerializeToElement(false) },
        Enabled: true,
        Archived: false,
        OffVariant: "off",
        Rules: rules,
        Fallthrough: Serve.Single(on ? "on" : "off"));

    public static FlagSnapshot Of<T>(string key, FlagType type, T value, T other) => new(
        key,
        type,
        new Dictionary<string, JsonElement> { ["a"] = JsonSerializer.SerializeToElement(value), ["b"] = JsonSerializer.SerializeToElement(other) },
        Enabled: true,
        Archived: false,
        OffVariant: "b",
        Rules: [],
        Fallthrough: Serve.Single("a"));

    public static Rule Rule(string id, string attribute, string value, string variant) =>
        new(id, null, [new Condition(attribute, ConditionOperator.Equals, [value])], Serve.Single(variant));
}
