using System.Net;
using System.Text.Json;
using Bunting.Evaluation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Bunting.Server.Tests;

[Collection("server")]
public class RealtimeEndpointTests(ServerFixture fixture)
{
    private readonly Api _api = fixture.Client();

    private static string Cfg(string project, string flag) => $"/api/v1/projects/{project}/environments/production/flags/{flag}";

    private async Task<(string Project, HttpClient Server, HttpClient Client)> Setup()
    {
        var project = await _api.NewProject();
        await _api.NewBoolFlag(project, "promo", clientSide: true);
        await _api.Put(
            Cfg(project, "promo"),
            new
            {
                enabled = true,
                offVariant = "off",
                rules = new[] { new { id = "tr", conditions = new[] { new { attribute = "country", @operator = "Equals", values = new[] { "TR" } } }, serve = new { variant = "on" } } },
                fallthrough = new { variant = "off" },
            },
            Api.ETag(await _api.Get(Cfg(project, "promo"))));
        await _api.Post($"/api/v1/projects/{project}/environments/production/segments", new { key = "vips", included = new[] { "alice" } });
        var (_, server) = await _api.NewKey(project, "production", "Server");
        var (_, client) = await _api.NewKey(project, "production", "Client");
        return (project, fixture.WithKey(server), fixture.WithKey(client));
    }

    [Fact]
    public async Task Ruleset_contains_everything_needed_to_evaluate_locally_and_matches_ofrep()
    {
        var (_, server, _) = await Setup();
        var response = await server.GetAsync("/api/v1/sdk/ruleset");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var snapshot = JsonSerializer.Deserialize<RulesetSnapshot>(await response.Content.ReadAsStringAsync(), Api.Json)!;
        Assert.Equal(long.Parse(response.Headers.ETag!.Tag.Trim('"')), snapshot.Version);
        Assert.Equal("vips", snapshot.Segments.Single().Key);

        var local = CompiledRuleset.Compile(snapshot);
        Assert.Empty(local.Diagnostics);
        foreach (var country in new[] { "TR", "DE" })
        {
            var remote = await Api.Body(await server.Ofrep("/ofrep/v1/evaluate/flags/promo", new { targetingKey = "u", country }));
            var result = local.Evaluate("promo", new EvaluationContext("u", new Dictionary<string, object?> { ["country"] = country }));
            Assert.Equal(remote.GetProperty("value").GetBoolean(), result.Value.GetBoolean());
            Assert.Equal(remote.GetProperty("reason").GetString(), result.Reason.ToWireString());
            Assert.Equal(remote.GetProperty("variant").GetString(), result.Variant);
        }
    }

    [Fact]
    public async Task Ruleset_supports_conditional_requests()
    {
        var (project, server, _) = await Setup();
        var first = await server.GetAsync("/api/v1/sdk/ruleset");
        var etag = first.Headers.ETag!.Tag;

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sdk/ruleset");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var unchanged = await server.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);

        await _api.Patch(Cfg(project, "promo"), new { enabled = false }, Api.ETag(await _api.Get(Cfg(project, "promo"))));
        var again = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sdk/ruleset");
        again.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var changed = await server.SendAsync(again);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(etag, changed.Headers.ETag!.Tag);
    }

    [Theory]
    [InlineData("/api/v1/sdk/ruleset")]
    [InlineData("/api/v1/sdk/stream")]
    public async Task Sdk_endpoints_require_a_server_key(string path)
    {
        var (_, server, client) = await Setup();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Factory.CreateClient().GetAsync(path)).StatusCode);

        var management = fixture.Factory.CreateClient();
        management.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ServerFixture.OwnerToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await management.GetAsync(path, HttpCompletionOption.ResponseHeadersRead)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.GetAsync(path, HttpCompletionOption.ResponseHeadersRead)).StatusCode);
    }

    [Fact]
    public async Task Ruleset_download_never_leaks_across_environments()
    {
        var (project, _, _) = await Setup();
        var (_, devKey) = await _api.NewKey(project, "development", "Server");
        var snapshot = JsonSerializer.Deserialize<RulesetSnapshot>(
            await fixture.WithKey(devKey).GetStringAsync("/api/v1/sdk/ruleset"), Api.Json)!;
        Assert.Empty(snapshot.Segments); // the segment was created in production only
        Assert.False(snapshot.Flags.Single().Enabled);
    }

    [Fact]
    public async Task Server_stream_sends_the_current_version_then_changes()
    {
        var (project, server, _) = await Setup();
        await using var stream = await SseClient.ConnectAsync(server, "/api/v1/sdk/stream");

        var initial = await stream.NextAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ruleset-updated", initial!.Name);
        var version = SseClient.VersionOf(initial);
        Assert.Equal(await _api.RulesetVersion(project, "production"), version);

        await _api.Patch(Cfg(project, "promo"), new { enabled = false }, Api.ETag(await _api.Get(Cfg(project, "promo"))));

        var changed = await stream.NextAsync(TimeSpan.FromSeconds(5), e => e.Name == "ruleset-updated");
        Assert.Equal(version + 1, SseClient.VersionOf(changed!));
    }

    [Fact]
    public async Task Changes_in_other_environments_do_not_wake_the_stream()
    {
        var (project, server, _) = await Setup();
        await _api.NewBoolFlag(project, "dev-only");
        await using var stream = await SseClient.ConnectAsync(server, "/api/v1/sdk/stream");
        await stream.NextAsync(TimeSpan.FromSeconds(5));

        var dev = $"/api/v1/projects/{project}/environments/development/flags/dev-only";
        await _api.Patch(dev, new { enabled = true }, Api.ETag(await _api.Get(dev)));

        Assert.Null(await stream.NextAsync(TimeSpan.FromMilliseconds(700), e => e.Name == "ruleset-updated"));
    }

    [Fact]
    public async Task Idle_streams_receive_heartbeats()
    {
        var (_, server, _) = await Setup();
        // Re-use the same database and server code, but with a 1 second heartbeat.
        using var factory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions.Configure<Bunting.Server.Features.Realtime.StreamOptions>(s, o => o.HeartbeatSeconds = 0.5)));
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-API-Key", server.DefaultRequestHeaders.GetValues("X-API-Key").Single());

        await using var stream = await SseClient.ConnectAsync(http, "/api/v1/sdk/stream");
        var heartbeat = await stream.NextAsync(TimeSpan.FromSeconds(5), e => e.Name == "heartbeat");
        Assert.NotNull(heartbeat);
        Assert.NotNull(await stream.NextAsync(TimeSpan.FromSeconds(5), e => e.Name == "heartbeat"));
    }

    [Fact]
    public async Task Client_stream_only_announces_that_flags_changed()
    {
        var (project, _, client) = await Setup();
        await using var stream = await SseClient.ConnectAsync(client, "/api/v1/client/stream");
        var initial = await stream.NextAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("flags-changed", initial!.Name);

        await _api.Patch(Cfg(project, "promo"), new { enabled = false }, Api.ETag(await _api.Get(Cfg(project, "promo"))));
        var changed = await stream.NextAsync(TimeSpan.FromSeconds(5), e => SseClient.VersionOf(e) > SseClient.VersionOf(initial));
        Assert.NotNull(changed);

        // Rules, segments and flag data never travel on the client stream.
        foreach (var e in new[] { initial, changed })
        {
            Assert.Equal(["version"], JsonDocument.Parse(e.Data).RootElement.EnumerateObject().Select(p => p.Name));
        }
    }

    [Fact]
    public async Task Client_stream_accepts_the_key_as_a_query_parameter_but_server_keys_are_not_accepted_there()
    {
        var (project, _, client) = await Setup();
        var clientKey = client.DefaultRequestHeaders.GetValues("X-API-Key").Single();
        var (_, serverKey) = await _api.NewKey(project, "production", "Server");

        var anonymous = fixture.Factory.CreateClient();
        await using var stream = await SseClient.ConnectAsync(anonymous, $"/api/v1/client/stream?apiKey={clientKey}");
        Assert.NotNull(await stream.NextAsync(TimeSpan.FromSeconds(5)));

        var viaQuery = await anonymous.GetAsync($"/api/v1/client/stream?apiKey={serverKey}", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.Unauthorized, viaQuery.StatusCode);
        var wrongPath = await anonymous.GetAsync($"/api/v1/sdk/ruleset?apiKey={serverKey}");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPath.StatusCode);
    }

    [Fact]
    public async Task Revoked_keys_cannot_open_new_streams()
    {
        var (project, _, _) = await Setup();
        var (id, key) = await _api.NewKey(project, "production", "Server");
        await _api.Delete($"/api/v1/projects/{project}/environments/production/api-keys/{id}");
        var response = await fixture.WithKey(key).GetAsync("/api/v1/sdk/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
