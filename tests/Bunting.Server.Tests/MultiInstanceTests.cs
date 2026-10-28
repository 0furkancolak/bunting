using System.Diagnostics;
using System.Net;
using Npgsql;

namespace Bunting.Server.Tests;

/// <summary>
/// Two server instances share one database: this process (the in-memory test host) and a second, real process.
/// A change made through one must reach SSE clients of the other quickly.
/// </summary>
[Collection("server")]
public class MultiInstanceTests(ServerFixture fixture)
{
    private const string SecondInstanceAdminToken = "second-instance-admin";

    private readonly Api _api = fixture.Client();

    private static string Cfg(string project, string flag) => $"/api/v1/projects/{project}/environments/production/flags/{flag}";

    private async Task<(string Project, string ServerKey)> Setup()
    {
        var project = await _api.NewProject();
        await _api.NewBoolFlag(project, "promo");
        var (_, key) = await _api.NewKey(project, "production", "Server");
        return (project, key);
    }

    private async Task<long> Toggle(Api api, string project, bool enabled)
    {
        var url = Cfg(project, "promo");
        var response = await api.Patch(url, new { enabled }, Api.ETag(await api.Get(url)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await _api.RulesetVersion(project, "production");
    }

    private static HttpClient Http(string baseUrl, string key)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Add("X-API-Key", key);
        return http;
    }

    [Fact]
    public async Task A_change_on_one_instance_reaches_sse_clients_of_the_other_in_under_a_second()
    {
        var (project, key) = await Setup();
        await using var second = await ServerProcess.StartAsync(fixture.ConnectionString, SecondInstanceAdminToken);
        using var remote = Http(second.BaseUrl, key);
        await using var remoteStream = await SseClient.ConnectAsync(remote, "/api/v1/sdk/stream");
        await using var localStream = await SseClient.ConnectAsync(fixture.WithKey(key), "/api/v1/sdk/stream");
        Assert.NotNull(await remoteStream.NextAsync(TimeSpan.FromSeconds(10)));
        Assert.NotNull(await localStream.NextAsync(TimeSpan.FromSeconds(10)));

        // Warm both paths once so the measured round trip is steady state, not JIT.
        await Toggle(_api, project, true);
        await remoteStream.NextAsync(TimeSpan.FromSeconds(5), e => e.Name == "ruleset-updated");
        await localStream.NextAsync(TimeSpan.FromSeconds(5), e => e.Name == "ruleset-updated");

        for (var round = 0; round < 3; round++)
        {
            var started = Stopwatch.StartNew();
            var version = await Toggle(_api, project, round % 2 == 1);
            var onRemote = await remoteStream.NextAsync(TimeSpan.FromSeconds(5), e => SseClient.VersionOf(e) >= version);
            var elapsed = started.Elapsed;

            Assert.NotNull(onRemote);
            Assert.Equal(version, SseClient.VersionOf(onRemote));
            Assert.True(elapsed < TimeSpan.FromSeconds(1), $"round {round}: change took {elapsed.TotalMilliseconds:F0} ms to reach the other instance");
            Assert.NotNull(await localStream.NextAsync(TimeSpan.FromSeconds(5), e => SseClient.VersionOf(e) >= version));
        }
    }

    [Fact]
    public async Task The_other_instance_serves_the_new_ruleset_as_soon_as_it_is_announced()
    {
        var (project, key) = await Setup();
        await using var second = await ServerProcess.StartAsync(fixture.ConnectionString, SecondInstanceAdminToken);
        using var remote = Http(second.BaseUrl, key);
        await using var stream = await SseClient.ConnectAsync(remote, "/api/v1/sdk/stream");
        await stream.NextAsync(TimeSpan.FromSeconds(10));

        // Prime the second instance's cache, then change through the first instance.
        var before = await remote.GetAsync("/api/v1/sdk/ruleset");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var version = await Toggle(_api, project, true);
        await stream.NextAsync(TimeSpan.FromSeconds(5), e => SseClient.VersionOf(e) >= version);

        var after = await remote.GetAsync("/api/v1/sdk/ruleset");
        Assert.Equal(version.ToString(System.Globalization.CultureInfo.InvariantCulture), after.Headers.ETag!.Tag.Trim('"'));
        Assert.Contains("\"enabled\":true", await after.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Changes_made_while_the_listener_connection_is_down_are_still_delivered()
    {
        var (project, key) = await Setup();
        await using var second = await ServerProcess.StartAsync(fixture.ConnectionString, SecondInstanceAdminToken);
        using var remote = Http(second.BaseUrl, key);
        await using var stream = await SseClient.ConnectAsync(remote, "/api/v1/sdk/stream");
        await stream.NextAsync(TimeSpan.FromSeconds(10));

        // Kill every LISTEN connection (both instances), then change immediately: the notification is lost for sure.
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await using var kill = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE pid <> pg_backend_pid() AND query LIKE 'LISTEN bunting_ruleset_changed%'",
                admin);
            await kill.ExecuteNonQueryAsync();
        }

        var version = await Toggle(_api, project, true);

        // The listener reconnects (backoff < 1 s) and reconciles versions from the database.
        var delivered = await stream.NextAsync(TimeSpan.FromSeconds(15), e => SseClient.VersionOf(e) >= version);
        Assert.NotNull(delivered);

        // And live notifications work again afterwards.
        var next = await Toggle(_api, project, false);
        Assert.NotNull(await stream.NextAsync(TimeSpan.FromSeconds(5), e => SseClient.VersionOf(e) >= next));
    }

    [Fact]
    public async Task Writes_made_through_the_second_instance_reach_clients_of_the_first()
    {
        var (project, key) = await Setup();
        await using var second = await ServerProcess.StartAsync(fixture.ConnectionString, SecondInstanceAdminToken);
        using var secondAdminHttp = new HttpClient { BaseAddress = new Uri(second.BaseUrl) };
        secondAdminHttp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SecondInstanceAdminToken);

        await using var stream = await SseClient.ConnectAsync(fixture.WithKey(key), "/api/v1/sdk/stream");
        await stream.NextAsync(TimeSpan.FromSeconds(5));

        // The first request to a fresh process pays JIT and connection costs; warm it up so the measurement is steady state.
        var viaSecond = new Api(secondAdminHttp);
        var warm = await Toggle(viaSecond, project, true);
        await stream.NextAsync(TimeSpan.FromSeconds(5), e => SseClient.VersionOf(e) >= warm);

        var started = Stopwatch.StartNew();
        var version = await Toggle(viaSecond, project, false);
        var received = await stream.NextAsync(TimeSpan.FromSeconds(5), e => SseClient.VersionOf(e) >= version);
        Assert.NotNull(received);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), $"took {started.ElapsedMilliseconds} ms");
    }
}
