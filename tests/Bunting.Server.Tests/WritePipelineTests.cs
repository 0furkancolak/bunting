using System.Net;
using System.Threading.Channels;
using Npgsql;

namespace Bunting.Server.Tests;

[Collection("server")]
public class WritePipelineTests(ServerFixture fixture)
{
    private readonly Api _api = fixture.Client();

    private static string ConfigUrl(string project, string flag, string env = "production") =>
        $"/api/v1/projects/{project}/environments/{env}/flags/{flag}";

    [Fact]
    public async Task Concurrent_updates_with_the_same_etag_yield_exactly_one_success()
    {
        var project = await _api.NewProject();
        await _api.NewBoolFlag(project, "race");
        var url = ConfigUrl(project, "race");
        var version = Api.ETag(await _api.Get(url));
        var rulesetBefore = await _api.RulesetVersion(project, "production");

        var attempts = Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => fixture.Client().Put(
                url,
                new { enabled = i % 2 == 0, offVariant = "off", rules = Array.Empty<object>(), fallthrough = new { variant = "off" } },
                version)))
            .ToArray();
        var responses = await Task.WhenAll(attempts);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(rulesetBefore + 1, await _api.RulesetVersion(project, "production"));
        Assert.Equal(version + 1, Api.ETag(await _api.Get(url)));
    }

    [Fact]
    public async Task Every_write_creates_an_audit_entry_with_before_and_after()
    {
        var project = await _api.NewProject();
        var flagVersion = await _api.NewBoolFlag(project, "audited");
        var url = ConfigUrl(project, "audited");
        await _api.Patch(url, new { enabled = true }, Api.ETag(await _api.Get(url)));
        await _api.Put(
            $"/api/v1/projects/{project}/flags/audited",
            new { name = "New name", variants = new { on = true, off = false }, clientSideAvailable = false, archived = false },
            flagVersion);

        var page = await Api.Body(await _api.Get($"/api/v1/projects/{project}/audit"));
        var items = page.GetProperty("items").EnumerateArray().ToList();
        var actions = items.Select(i => $"{i.GetProperty("entityType").GetString()}:{i.GetProperty("action").GetString()}").ToList();

        Assert.Equal(["flag:updated", "flag-config:enabled", "flag:created", "project:created"], actions);
        Assert.All(items, i => Assert.Equal("owner-user", i.GetProperty("actor").GetString()));

        var enabled = items[1];
        Assert.Equal("production", enabled.GetProperty("environment").GetString());
        Assert.False(enabled.GetProperty("before").GetProperty("enabled").GetBoolean());
        Assert.True(enabled.GetProperty("after").GetProperty("enabled").GetBoolean());

        var renamed = items[0];
        Assert.Equal("audited", renamed.GetProperty("before").GetProperty("name").GetString());
        Assert.Equal("New name", renamed.GetProperty("after").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Audit_can_be_filtered_and_paged()
    {
        var project = await _api.NewProject();
        await _api.NewBoolFlag(project, "one");
        await _api.NewBoolFlag(project, "two");

        async Task<System.Text.Json.JsonElement> Query(string q) => await Api.Body(await _api.Get($"/api/v1/projects/{project}/audit{q}"));

        Assert.Single((await Query("?entityType=flag&entityId=one")).GetProperty("items").EnumerateArray());
        Assert.Empty((await Query("?actor=nobody")).GetProperty("items").EnumerateArray());

        var first = await Query("?limit=2");
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        var next = first.GetProperty("nextBefore").GetString();
        var second = await Query($"?limit=2&before={Uri.EscapeDataString(next!)}");
        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, second.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task Failed_writes_leave_no_audit_no_version_bump_and_no_notification()
    {
        var project = await _api.NewProject();
        await _api.NewBoolFlag(project, "atomic");
        var url = ConfigUrl(project, "atomic");
        var version = Api.ETag(await _api.Get(url));
        var rulesetBefore = await _api.RulesetVersion(project, "production");
        var auditBefore = (await Api.Body(await _api.Get($"/api/v1/projects/{project}/audit"))).GetProperty("items").GetArrayLength();

        var envIds = (await Api.Body(await _api.Get($"/api/v1/projects/{project}/environments")))
            .EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToHashSet();
        await using var listener = await Listen();
        var invalid = await _api.Put(
            url,
            new { enabled = true, offVariant = "ghost", rules = Array.Empty<object>(), fallthrough = new { variant = "off" } },
            version);
        var stale = await _api.Patch(url, new { enabled = true }, version + 99);

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(rulesetBefore, await _api.RulesetVersion(project, "production"));
        Assert.Equal(auditBefore, (await Api.Body(await _api.Get($"/api/v1/projects/{project}/audit"))).GetProperty("items").GetArrayLength());
        Assert.Null(await listener.NextAsync(TimeSpan.FromMilliseconds(500), p => envIds.Contains(p.Split(':')[0])));
    }

    [Fact]
    public async Task Successful_write_notifies_with_environment_id_and_new_version()
    {
        var project = await _api.NewProject();
        await _api.NewBoolFlag(project, "notify");
        var url = ConfigUrl(project, "notify");
        var env = await Api.Body(await _api.Get($"/api/v1/projects/{project}/environments/production"));
        var envId = env.GetProperty("id").GetGuid();

        await using var listener = await Listen();
        var response = await _api.Patch(url, new { enabled = true }, Api.ETag(await _api.Get(url)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var expectedVersion = await _api.RulesetVersion(project, "production");
        var payload = await listener.NextAsync(TimeSpan.FromSeconds(5), p => p.StartsWith($"{envId}:", StringComparison.Ordinal) && p != $"{envId}:{expectedVersion - 1}");
        Assert.Equal($"{envId}:{expectedVersion}", payload);
    }

    [Fact]
    public async Task Flag_level_changes_notify_every_environment()
    {
        var project = await _api.NewProject();
        var envIds = (await Api.Body(await _api.Get($"/api/v1/projects/{project}/environments")))
            .EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToHashSet();

        await using var listener = await Listen();
        await _api.NewBoolFlag(project, "everywhere");

        var seen = new HashSet<Guid>();
        while (await listener.NextAsync(TimeSpan.FromSeconds(3), p => envIds.Contains(Guid.Parse(p.Split(':')[0]))) is { } payload)
        {
            seen.Add(Guid.Parse(payload.Split(':')[0]));
            if (seen.SetEquals(envIds))
            {
                break;
            }
        }

        Assert.Equal(envIds, seen);
    }

    private async Task<Listener> Listen()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var channel = Channel.CreateUnbounded<string>();
        connection.Notification += (_, e) => channel.Writer.TryWrite(e.Payload);
        await using (var cmd = new NpgsqlCommand("LISTEN bunting_ruleset_changed", connection))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        var cts = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await connection.WaitAsync(cts.Token);
                }
            }
            catch (Exception)
            {
                // cancelled on dispose
            }
        });
        return new Listener(connection, channel, pump, cts);
    }

    private sealed class Listener(NpgsqlConnection connection, Channel<string> channel, Task pump, CancellationTokenSource cts) : IAsyncDisposable
    {
        /// <summary>Next payload accepted by <paramref name="accept"/>; notifications of other tests are skipped.</summary>
        public async Task<string?> NextAsync(TimeSpan timeout, Func<string, bool> accept)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var payload = await channel.Reader.ReadAsync(cts.Token);
                    if (accept(payload))
                    {
                        return payload;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            await pump;
            await connection.DisposeAsync();
            cts.Dispose();
        }
    }
}
