using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Bunting.Server.Tests;

/// <summary>One Postgres container and one in-process server shared by all integration tests.</summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public const string OwnerToken = "test-owner-token";
    public const string EditorToken = "test-editor-token";
    public const string ViewerToken = "test-viewer-token";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private WebApplicationFactory<Program>? _factory;

    public string ConnectionString => _postgres.GetConnectionString();

    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Not initialised.");

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        // Environment variables are read by WebApplication.CreateBuilder before Program continues, unlike late test overrides.
        Environment.SetEnvironmentVariable("ConnectionStrings__bunting", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Warning");
        string[] roles = ["Owner", "Editor", "Viewer"];
        string[] tokens = [OwnerToken, EditorToken, ViewerToken];
        for (var i = 0; i < roles.Length; i++)
        {
            Environment.SetEnvironmentVariable($"Bunting__Management__Tokens__{i}__Name", $"{roles[i].ToLowerInvariant()}-user");
            Environment.SetEnvironmentVariable($"Bunting__Management__Tokens__{i}__Token", tokens[i]);
            Environment.SetEnvironmentVariable($"Bunting__Management__Tokens__{i}__Role", roles[i]);
        }

        _factory = new WebApplicationFactory<Program>();
        _ = _factory.Server; // starts the host and applies migrations
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    public Api Client(string token = OwnerToken)
    {
        var http = Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new Api(http);
    }
}

[CollectionDefinition("server")]
public sealed class ServerCollection : ICollectionFixture<ServerFixture>;

public sealed class Api(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpClient Http { get; } = http;

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, long? ifMatch = null)
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

        return Http.SendAsync(request);
    }

    public Task<HttpResponseMessage> Get(string url) => SendAsync(HttpMethod.Get, url);

    public Task<HttpResponseMessage> Post(string url, object body) => SendAsync(HttpMethod.Post, url, body);

    public Task<HttpResponseMessage> Put(string url, object body, long? ifMatch) => SendAsync(HttpMethod.Put, url, body, ifMatch);

    public Task<HttpResponseMessage> Patch(string url, object body, long? ifMatch = null) => SendAsync(HttpMethod.Patch, url, body, ifMatch);

    public Task<HttpResponseMessage> Delete(string url, long? ifMatch = null) => SendAsync(HttpMethod.Delete, url, null, ifMatch);

    public static async Task<JsonElement> Body(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json);

    public static long ETag(HttpResponseMessage response) =>
        long.Parse(response.Headers.ETag!.Tag.Trim('"'));

    /// <summary>Creates a project with the three default environments and returns its unique key.</summary>
    public async Task<string> NewProject()
    {
        var key = "p-" + Guid.NewGuid().ToString("N")[..12];
        var response = await Post("/api/v1/projects", new { key, name = "Test " + key });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return key;
    }

    /// <summary>Creates a boolean flag (variants on/off) and returns its ETag version.</summary>
    public async Task<long> NewBoolFlag(string project, string key, bool clientSide = false)
    {
        var response = await Post(
            $"/api/v1/projects/{project}/flags",
            new
            {
                key,
                name = key,
                type = "Boolean",
                variants = new Dictionary<string, bool> { ["on"] = true, ["off"] = false },
                defaultVariant = "off",
                clientSideAvailable = clientSide,
            });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return ETag(response);
    }

    public async Task<long> RulesetVersion(string project, string environment) =>
        (await Body(await Get($"/api/v1/projects/{project}/environments/{environment}"))).GetProperty("rulesetVersion").GetInt64();
}

public static class ApiExtensions
{
    public static async Task<(Guid Id, string Key)> NewKey(this Api api, string project, string environment, string type)
    {
        var response = await api.Post($"/api/v1/projects/{project}/environments/{environment}/api-keys", new { name = type + " key", type });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var body = await Api.Body(response);
        return (body.GetProperty("id").GetGuid(), body.GetProperty("key").GetString()!);
    }

    public static HttpClient WithKey(this ServerFixture fixture, string key, bool bearer = false)
    {
        var http = fixture.Factory.CreateClient();
        if (bearer)
        {
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }
        else
        {
            http.DefaultRequestHeaders.Add("X-API-Key", key);
        }

        return http;
    }

    public static Task<HttpResponseMessage> Ofrep(this HttpClient http, string path, object context, string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = System.Net.Http.Json.JsonContent.Create(new { context }, options: Api.Json) };
        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return http.SendAsync(request);
    }
}
