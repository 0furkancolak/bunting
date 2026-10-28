using System.Net;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Bunting.Server.Tests;

[Collection("server")]
public class ApiKeyTests(ServerFixture fixture)
{
    private readonly Api _api = fixture.Client();

    private static string KeysUrl(string project, string env = "production") => $"/api/v1/projects/{project}/environments/{env}/api-keys";

    [Theory]
    [InlineData("Server", "bnt_srv_")]
    [InlineData("Client", "bnt_cli_")]
    public async Task Keys_have_the_documented_prefix_and_are_shown_once(string type, string prefix)
    {
        var project = await _api.NewProject();
        var (id, key) = await _api.NewKey(project, "production", type);

        Assert.StartsWith(prefix, key);
        Assert.Equal(prefix.Length + 43, key.Length);

        var list = await Api.Body(await _api.Get(KeysUrl(project)));
        var item = list.EnumerateArray().Single();
        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal(key[^4..], item.GetProperty("hint").GetString());
        Assert.DoesNotContain(key, list.GetRawText());
    }

    [Fact]
    public async Task Only_the_sha256_hash_is_stored()
    {
        var project = await _api.NewProject();
        var (id, key) = await _api.NewKey(project, "production", "Server");

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT \"KeyHash\", \"Hint\" FROM api_keys WHERE \"Id\" = @id", connection);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(key)), (byte[])reader[0]);
        Assert.Equal(4, reader.GetString(1).Length);
    }

    [Fact]
    public async Task The_audit_log_never_contains_the_key()
    {
        var project = await _api.NewProject();
        var (id, key) = await _api.NewKey(project, "production", "Client");
        await _api.Delete($"{KeysUrl(project)}/{id}");

        var audit = await (await _api.Get($"/api/v1/projects/{project}/audit")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(key, audit);
        Assert.Contains("\"revoked\"", audit);
        Assert.Contains("\"api-key\"", audit);
    }

    [Fact]
    public async Task Revoked_keys_stop_working_immediately()
    {
        var project = await _api.NewProject();
        await _api.NewBoolFlag(project, "f");
        var (id, key) = await _api.NewKey(project, "production", "Server");
        var http = fixture.WithKey(key);

        Assert.Equal(HttpStatusCode.OK, (await http.Ofrep("/ofrep/v1/evaluate/flags", new { targetingKey = "u" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _api.Delete($"{KeysUrl(project)}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.Ofrep("/ofrep/v1/evaluate/flags", new { targetingKey = "u" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _api.Delete($"{KeysUrl(project)}/{id}")).StatusCode); // idempotent
    }

    [Fact]
    public async Task Both_ofrep_authentication_mechanisms_are_accepted()
    {
        var project = await _api.NewProject();
        var (_, key) = await _api.NewKey(project, "production", "Server");
        foreach (var bearer in new[] { false, true })
        {
            var http = fixture.WithKey(key, bearer);
            Assert.Equal(HttpStatusCode.OK, (await http.Ofrep("/ofrep/v1/evaluate/flags", new { targetingKey = "u" })).StatusCode);
        }
    }

    [Fact]
    public async Task Invalid_unknown_and_missing_keys_are_401()
    {
        var project = await _api.NewProject();
        var (_, key) = await _api.NewKey(project, "production", "Server");
        var tampered = key[..^1] + (key[^1] == 'a' ? 'b' : 'a');

        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.WithKey(tampered).Ofrep("/ofrep/v1/evaluate/flags", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.WithKey("bnt_srv_nonsense").Ofrep("/ofrep/v1/evaluate/flags", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Factory.CreateClient().Ofrep("/ofrep/v1/evaluate/flags", new { })).StatusCode);
    }

    [Fact]
    public async Task Management_tokens_and_api_keys_are_not_interchangeable()
    {
        var project = await _api.NewProject();
        var (_, key) = await _api.NewKey(project, "production", "Server");

        var asToken = fixture.Factory.CreateClient();
        asToken.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ServerFixture.OwnerToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await asToken.Ofrep("/ofrep/v1/evaluate/flags", new { })).StatusCode);

        var managementWithKey = new Api(fixture.WithKey(key, bearer: true));
        Assert.Equal(HttpStatusCode.Unauthorized, (await managementWithKey.Get("/api/v1/projects")).StatusCode);
    }

    [Fact]
    public async Task Key_management_is_owner_only_and_validated()
    {
        var project = await _api.NewProject();
        foreach (var token in new[] { ServerFixture.ViewerToken, ServerFixture.EditorToken })
        {
            var other = fixture.Client(token);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.Get(KeysUrl(project))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.Post(KeysUrl(project), new { name = "x", type = "Server" })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await _api.Post(KeysUrl(project), new { name = "", type = "Server" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.Delete($"{KeysUrl(project)}/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Keys_are_scoped_to_their_environment()
    {
        var project = await _api.NewProject();
        await _api.NewBoolFlag(project, "f");
        var cfg = $"/api/v1/projects/{project}/environments/production/flags/f";
        await _api.Patch(cfg, new { enabled = true }, Api.ETag(await _api.Get(cfg)));
        var (_, devKey) = await _api.NewKey(project, "development", "Server");

        var body = await Api.Body(await fixture.WithKey(devKey).Ofrep("/ofrep/v1/evaluate/flags/f", new { targetingKey = "u" }));
        Assert.Equal("DISABLED", body.GetProperty("reason").GetString()); // development is still off
    }
}
