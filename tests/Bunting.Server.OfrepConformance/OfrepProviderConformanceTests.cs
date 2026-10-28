using System.Diagnostics;
using System.Text.Json;

namespace Bunting.Server.OfrepConformance;

/// <summary>
/// Evaluates against a real Bunting server through the official OpenFeature JS SDK and the community OFREP providers
/// (server provider with dynamic context, web provider with bulk evaluation).
/// </summary>
[Collection("conformance")]
public class OfrepProviderConformanceTests(ConformanceFixture fixture)
{
    private static readonly string NodeDir = Path.Combine(ServerProcess.RepoRoot, "tests", "Bunting.Server.OfrepConformance", "node");

    private async Task<JsonElement> RunNode()
    {
        if (!Directory.Exists(Path.Combine(NodeDir, "node_modules")))
        {
            await Run("npm", "ci --no-audit --no-fund");
        }

        var stdout = await Run("node", $"run.mjs {fixture.BaseUrl} {fixture.ServerKey} {fixture.ClientKey} bnt_srv_invalidinvalidinvalid");
        return JsonDocument.Parse(stdout.Trim().Split('\n')[^1]).RootElement.Clone();
    }

    private static async Task<string> Run(string command, string arguments)
    {
        var info = new ProcessStartInfo(command, arguments) { WorkingDirectory = NodeDir, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {command}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, $"{command} {arguments} failed ({process.ExitCode}):\n{await stderr}\n{await stdout}");
        return await stdout;
    }

    private static string? Str(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetString();

    [Fact]
    public async Task Official_providers_work_against_Bunting()
    {
        var result = await RunNode();
        var server = result.GetProperty("server");

        var match = server.GetProperty("boolMatch");
        Assert.True(match.GetProperty("value").GetBoolean());
        Assert.Equal("TARGETING_MATCH", Str(match, "reason"));
        Assert.Equal("on", Str(match, "variant"));
        Assert.Equal("turkey", Str(match, "ruleId"));
        Assert.Null(Str(match, "errorCode"));

        var fallthrough = server.GetProperty("boolDefault");
        Assert.False(fallthrough.GetProperty("value").GetBoolean());
        Assert.Equal("DEFAULT", Str(fallthrough, "reason"));

        Assert.Equal("hello", server.GetProperty("string").GetProperty("value").GetString());
        Assert.Equal(7, server.GetProperty("integer").GetProperty("value").GetInt32());
        Assert.Equal(1.5, server.GetProperty("double").GetProperty("value").GetDouble());
        var obj = server.GetProperty("object").GetProperty("value");
        Assert.Equal(10, obj.GetProperty("limit").GetInt32());
        Assert.Equal("x", obj.GetProperty("tags")[0].GetString());

        var missing = server.GetProperty("missing");
        Assert.True(missing.GetProperty("value").GetBoolean()); // code default
        Assert.Equal("FLAG_NOT_FOUND", Str(missing, "errorCode"));
        Assert.Equal("ERROR", Str(missing, "reason"));

        var mismatch = server.GetProperty("typeMismatch");
        Assert.Equal("code-default", mismatch.GetProperty("value").GetString());
        Assert.Equal("TYPE_MISMATCH", Str(mismatch, "errorCode"));

        var noKey = server.GetProperty("noTargetingKey");
        Assert.True(noKey.GetProperty("value").GetBoolean()); // code default
        Assert.Equal("TARGETING_KEY_MISSING", Str(noKey, "errorCode"));

        var gradual = server.GetProperty("gradual");
        Assert.True(gradual.GetProperty("value").GetBoolean());
        Assert.Equal("SPLIT", Str(gradual, "reason"));
    }

    [Fact]
    public async Task An_invalid_key_degrades_to_code_defaults_instead_of_throwing()
    {
        var bad = (await RunNode()).GetProperty("badKey");
        var evaluation = bad.GetProperty("evaluation");
        Assert.True(evaluation.GetProperty("value").GetBoolean()); // code default
        Assert.Equal("ERROR", Str(evaluation, "reason"));
        Assert.NotNull(Str(evaluation, "errorCode"));
    }

    [Fact]
    public async Task Web_provider_with_a_client_key_gets_only_client_side_flags()
    {
        var web = (await RunNode()).GetProperty("web");

        var visible = web.GetProperty("visible");
        Assert.True(visible.GetProperty("value").GetBoolean());
        Assert.Equal("TARGETING_MATCH", Str(visible, "reason"));
        Assert.Null(Str(visible, "ruleId")); // rule details never reach client keys

        var hidden = web.GetProperty("hidden");
        Assert.True(hidden.GetProperty("value").GetBoolean()); // code default: the flag does not exist for this key
        Assert.Equal("FLAG_NOT_FOUND", Str(hidden, "errorCode"));
        Assert.Equal("FLAG_NOT_FOUND", Str(web.GetProperty("missing"), "errorCode"));

        var after = web.GetProperty("afterContextChange");
        Assert.False(after.GetProperty("value").GetBoolean()); // re-evaluated for a German user
        Assert.Equal("DEFAULT", Str(after, "reason"));
    }
}
