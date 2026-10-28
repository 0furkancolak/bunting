using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Testcontainers.PostgreSql;

namespace Bunting.Sdk.Tests;

/// <summary>The SDK against the real server process and PostgreSQL, including a server stop and restart.</summary>
public sealed class RealServerTests : IAsyncLifetime
{
    private const string AdminToken = "sdk-e2e-admin";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    private static HttpClient Management(string baseUrl)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AdminToken);
        return http;
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task SetEnabled(HttpClient management, string project, bool enabled)
    {
        var url = $"/api/v1/projects/{project}/environments/production/flags/promo";
        var current = await management.GetAsync(url);
        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(new { enabled, offVariant = "off", rules = Array.Empty<object>(), fallthrough = new { variant = "on" } }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        await Ok(await management.SendAsync(request));
    }

    [Fact]
    public async Task The_sample_scenario_survives_a_server_outage_and_reflects_changes_within_a_second()
    {
        var port = ServerProcess.FreeTcpPort();
        var server = await ServerProcess.StartAsync(_postgres.GetConnectionString(), AdminToken, port: port);
        try
        {
            using var management = Management(server.BaseUrl);
            const string project = "e2e";
            await Ok(await management.PostAsJsonAsync("/api/v1/projects", new { key = project, name = "E2E" }));
            await Ok(await management.PostAsJsonAsync(
                $"/api/v1/projects/{project}/flags",
                new
                {
                    key = "promo",
                    name = "promo",
                    type = "Boolean",
                    variants = new Dictionary<string, bool> { ["on"] = true, ["off"] = false },
                    defaultVariant = "off",
                }));
            var key = (await Ok(await management.PostAsJsonAsync(
                $"/api/v1/projects/{project}/environments/production/api-keys",
                new { name = "sdk", type = "Server" }))).GetProperty("key").GetString()!;
            await SetEnabled(management, project, enabled: true);

            await using var harness = await Harness.StartAsync(new BuntingOptions
            {
                ServerUrl = new Uri(server.BaseUrl),
                ApiKey = key,
                PollingInterval = TimeSpan.FromMilliseconds(500),
                MinReconnectDelay = TimeSpan.FromMilliseconds(100),
                MaxReconnectDelay = TimeSpan.FromSeconds(1),
                StaleAfter = TimeSpan.FromSeconds(1),
            });
            await Harness.Eventually(() => harness.Provider.ConnectionState == ConnectionState.Streaming);
            var first = await harness.Client.GetBooleanDetailsAsync("promo", false);
            Assert.True(first.Value, $"{first.Reason} {first.ErrorType} {first.ErrorMessage} v={harness.Provider.RulesetVersion}");

            // A change reaches the application in under a second (after a warm-up round for the cold server).
            await SetEnabled(management, project, enabled: false);
            await Harness.Eventually(async () => !await harness.Client.GetBooleanValueAsync("promo", true));
            var clock = Stopwatch.StartNew();
            await SetEnabled(management, project, enabled: true);
            await Harness.Eventually(async () => await harness.Client.GetBooleanValueAsync("promo", false), TimeSpan.FromSeconds(1));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");

            // The server goes away: the application keeps serving the last ruleset.
            await server.DisposeAsync();
            await Harness.Eventually(() => harness.Events.Contains(OpenFeature.Constant.ProviderEventTypes.ProviderStale));
            Assert.True(await harness.Client.GetBooleanValueAsync("promo", false));

            // The server comes back (and a change was made while it was away): the SDK recovers on its own.
            server = await ServerProcess.StartAsync(_postgres.GetConnectionString(), AdminToken, port: port);
            using var restarted = Management(server.BaseUrl);
            await SetEnabled(restarted, project, enabled: false);
            await Harness.Eventually(async () => !await harness.Client.GetBooleanValueAsync("promo", true), TimeSpan.FromSeconds(20), "recovered after the restart");
            await Harness.Eventually(() => harness.Provider.ConnectionState == ConnectionState.Streaming);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }
}
