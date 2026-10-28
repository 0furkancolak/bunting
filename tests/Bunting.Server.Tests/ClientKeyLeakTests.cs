using System.Net;
using System.Text.Json;

namespace Bunting.Server.Tests;

/// <summary>Client keys must never reveal rules, segment members, rule ids or flags that are not client-side available.</summary>
[Collection("server")]
public class ClientKeyLeakTests(ServerFixture fixture)
{
    private const string SecretSegmentMember = "secret-member@corp.example";
    private const string SecretRuleId = "internal-vip-rule";
    private const string HiddenFlag = "internal-kill-switch";

    private readonly Api _api = fixture.Client();

    private async Task<(string Project, HttpClient Client, HttpClient Server)> Setup()
    {
        var project = await _api.NewProject();
        await _api.NewBoolFlag(project, "visible", clientSide: true);
        await _api.NewBoolFlag(project, HiddenFlag, clientSide: false);

        var segments = $"/api/v1/projects/{project}/environments/production/segments";
        Assert.Equal(HttpStatusCode.Created, (await _api.Post(segments, new { key = "vips", included = new[] { SecretSegmentMember } })).StatusCode);

        foreach (var flag in new[] { "visible", HiddenFlag })
        {
            var url = $"/api/v1/projects/{project}/environments/production/flags/{flag}";
            var put = await _api.Put(
                url,
                new
                {
                    enabled = true,
                    offVariant = "off",
                    rules = new[] { new { id = SecretRuleId + "-" + flag, conditions = new[] { new { attribute = "-", @operator = "InSegment", values = new[] { "vips" } } }, serve = new { variant = "on" } } },
                    fallthrough = new { variant = "off" },
                },
                Api.ETag(await _api.Get(url)));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        var (_, clientKey) = await _api.NewKey(project, "production", "Client");
        var (_, serverKey) = await _api.NewKey(project, "production", "Server");
        return (project, fixture.WithKey(clientKey), fixture.WithKey(serverKey));
    }

    private static void AssertNoSecrets(string text)
    {
        Assert.DoesNotContain(SecretSegmentMember, text);
        Assert.DoesNotContain(SecretRuleId, text);
        Assert.DoesNotContain(HiddenFlag, text);
        Assert.DoesNotContain("vips", text);
        Assert.DoesNotContain("InSegment", text);
        Assert.DoesNotContain("conditions", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bulk_for_a_client_key_only_contains_client_side_flags_and_no_rule_details()
    {
        var (_, client, _) = await Setup();
        var response = await client.Ofrep("/ofrep/v1/evaluate/flags", new { targetingKey = SecretSegmentMember });
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var flags = JsonDocument.Parse(text).RootElement.GetProperty("flags").EnumerateArray().ToList();
        Assert.Equal(["visible"], flags.Select(f => f.GetProperty("key").GetString()));
        Assert.True(flags[0].GetProperty("value").GetBoolean()); // the evaluation itself still works...
        Assert.False(flags[0].TryGetProperty("metadata", out _)); // ...but without the rule id
        AssertNoSecrets(text);
    }

    [Fact]
    public async Task Server_keys_see_all_flags_and_rule_ids()
    {
        var (_, _, server) = await Setup();
        var text = await (await server.Ofrep("/ofrep/v1/evaluate/flags", new { targetingKey = "x" })).Content.ReadAsStringAsync();
        Assert.Contains(HiddenFlag, text);
        var single = await (await server.Ofrep("/ofrep/v1/evaluate/flags/visible", new { targetingKey = SecretSegmentMember })).Content.ReadAsStringAsync();
        Assert.Contains(SecretRuleId, single);
    }

    [Fact]
    public async Task Hidden_flags_are_indistinguishable_from_missing_ones_for_client_keys()
    {
        var (_, client, _) = await Setup();
        var hidden = await client.Ofrep($"/ofrep/v1/evaluate/flags/{HiddenFlag}", new { targetingKey = SecretSegmentMember });
        var missing = await client.Ofrep("/ofrep/v1/evaluate/flags/does-not-exist", new { targetingKey = SecretSegmentMember });

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var hiddenBody = JsonDocument.Parse(await hidden.Content.ReadAsStringAsync()).RootElement;
        var missingBody = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(missingBody.GetProperty("errorCode").GetString(), hiddenBody.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("internal", hiddenBody.GetProperty("errorDetails").GetString()!.Replace(HiddenFlag, "<key>").Replace("<key>", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Single_evaluation_of_a_visible_flag_does_not_expose_the_rule()
    {
        var (_, client, _) = await Setup();
        var text = await (await client.Ofrep("/ofrep/v1/evaluate/flags/visible", new { targetingKey = SecretSegmentMember })).Content.ReadAsStringAsync();
        Assert.DoesNotContain(SecretRuleId, text);
        Assert.DoesNotContain(SecretSegmentMember, text);
        Assert.Contains("TARGETING_MATCH", text); // outcome is fine, internals are not
    }

    [Theory]
    [InlineData("/api/v1/projects")]
    [InlineData("/api/v1/projects/anything/flags")]
    [InlineData("/api/v1/sdk/ruleset")]
    [InlineData("/api/v1/sdk/stream")]
    public async Task Client_keys_cannot_reach_management_or_sdk_endpoints(string path)
    {
        var (_, client, _) = await Setup();
        var anyAuthFailure = new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound };
        Assert.Contains((await client.GetAsync(path)).StatusCode, anyAuthFailure);
        var bearer = fixture.WithKey(client.DefaultRequestHeaders.GetValues("X-API-Key").Single(), bearer: true);
        Assert.Contains((await bearer.GetAsync(path)).StatusCode, anyAuthFailure);
    }

    [Fact]
    public async Task Error_responses_do_not_leak_configuration()
    {
        var (_, client, _) = await Setup();
        foreach (var raw in new[] { "{}", "not json", "{\"context\":{\"x\":[1]}}" })
        {
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/ofrep/v1/evaluate/flags/{HiddenFlag}")
            {
                Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json"),
            });
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(SecretRuleId, text);
            Assert.DoesNotContain(SecretSegmentMember, text);
            Assert.DoesNotContain("vips", text);
        }
    }

    [Fact]
    public async Task A_client_key_never_sees_another_environments_flags()
    {
        var (project, _, _) = await Setup();
        var (_, devClient) = await _api.NewKey(project, "development", "Client");
        var text = await (await fixture.WithKey(devClient).Ofrep("/ofrep/v1/evaluate/flags", new { targetingKey = "x" })).Content.ReadAsStringAsync();
        var flags = JsonDocument.Parse(text).RootElement.GetProperty("flags").EnumerateArray().ToList();
        Assert.Equal(["visible"], flags.Select(f => f.GetProperty("key").GetString()));
        Assert.Equal("DISABLED", flags[0].GetProperty("reason").GetString()); // development was never enabled
    }
}
