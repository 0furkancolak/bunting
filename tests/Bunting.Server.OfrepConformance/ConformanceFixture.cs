using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Testcontainers.PostgreSql;

namespace Bunting.Server.OfrepConformance;

/// <summary>Runs the real server on a Kestrel port (so Node can reach it) against a throwaway Postgres.</summary>
public sealed class ConformanceFixture : IAsyncLifetime
{
    private const string AdminToken = "conformance-owner-token";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private ServerProcess? _server;

    public string BaseUrl { get; private set; } = string.Empty;

    public string ServerKey { get; private set; } = string.Empty;

    public string ClientKey { get; private set; } = string.Empty;

    public HttpClient Admin { get; private set; } = null!;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _server = await ServerProcess.StartAsync(_postgres.GetConnectionString(), AdminToken);
        BaseUrl = _server.BaseUrl;

        Admin = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        Admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        await Seed();
    }

    public async ValueTask DisposeAsync()
    {
        Admin?.Dispose();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    private async Task<JsonElement> Send(HttpMethod method, string url, object? body = null, long? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (ifMatch is { } v)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{v}\"");
        }

        var response = await Admin.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{method} {url} -> {(int)response.StatusCode}: {text}");
        if (response.Headers.ETag is { } etag)
        {
            LastETag = long.Parse(etag.Tag.Trim('"'));
        }

        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private long LastETag { get; set; }

    private async Task CreateFlag(string key, string type, object variants, string off, bool clientSide, object config)
    {
        await Send(HttpMethod.Post, "/api/v1/projects/conf/flags", new { key, name = key, type, variants, defaultVariant = off, clientSideAvailable = clientSide });
        var url = $"/api/v1/projects/conf/environments/production/flags/{key}";
        await Send(HttpMethod.Get, url);
        await Send(HttpMethod.Put, url, config, LastETag);
    }

    private async Task Seed()
    {
        await Send(HttpMethod.Post, "/api/v1/projects", new { key = "conf", name = "Conformance" });

        object Config(string off, object fallthrough, params object[] rules) =>
            new { enabled = true, offVariant = off, rules, fallthrough };
        object Rule(string id, string variant) => new
        {
            id,
            conditions = new[] { new { attribute = "country", @operator = "Equals", values = new[] { "TR" } } },
            serve = new { variant },
        };

        await CreateFlag("promo", "Boolean", new { on = true, off = false }, "off", false, Config("off", new { variant = "off" }, Rule("turkey", "on")));
        await CreateFlag("title", "String", new { a = "hello" }, "a", false, Config("a", new { variant = "a" }));
        await CreateFlag("limit", "Integer", new { a = 7 }, "a", false, Config("a", new { variant = "a" }));
        await CreateFlag("ratio", "Double", new { a = 1.5 }, "a", false, Config("a", new { variant = "a" }));
        await CreateFlag("config", "Object", new { a = new { limit = 10, tags = new[] { "x" } } }, "a", false, Config("a", new { variant = "a" }));
        await CreateFlag(
            "gradual",
            "Boolean",
            new { on = true, off = false },
            "off",
            false,
            Config("off", new { rollout = new { variants = new[] { new { variant = "on", weight = 10000 }, new { variant = "off", weight = 0 } } } }));
        await CreateFlag("visible", "Boolean", new { on = true, off = false }, "off", true, Config("off", new { variant = "off" }, Rule("turkey-client", "on")));
        await CreateFlag("internal", "Boolean", new { on = true, off = false }, "off", false, Config("off", new { variant = "on" }));

        ServerKey = (await Send(HttpMethod.Post, "/api/v1/projects/conf/environments/production/api-keys", new { name = "server", type = "Server" })).GetProperty("key").GetString()!;
        ClientKey = (await Send(HttpMethod.Post, "/api/v1/projects/conf/environments/production/api-keys", new { name = "client", type = "Client" })).GetProperty("key").GetString()!;
    }
}

[CollectionDefinition("conformance")]
public sealed class ConformanceCollection : ICollectionFixture<ConformanceFixture>;
