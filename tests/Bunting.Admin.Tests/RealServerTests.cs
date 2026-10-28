using System.Net;
using System.Text.RegularExpressions;
using Bunting.Admin.Api;
using Bunting.Admin.Infrastructure;
using Bunting.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Bunting.Admin.Tests;

/// <summary>The admin panel against the real server and PostgreSQL: sign-in, and what each role can do through the API client.</summary>
public sealed class RealServer : IAsyncLifetime
{
    public const string AdminEmail = "root@bunting.test";
    public const string AdminPassword = "bootstrap-password-1";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public ServerProcess Process { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        Process = await ServerProcess.StartAsync(
            _postgres.GetConnectionString(),
            "unused-static-token",
            new Dictionary<string, string>
            {
                ["Bunting__Bootstrap__AdminEmail"] = AdminEmail,
                ["Bunting__Bootstrap__AdminPassword"] = AdminPassword,
            });
    }

    public async ValueTask DisposeAsync()
    {
        await Process.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

public sealed partial class RealServerTests(RealServer realServer) : IClassFixture<RealServer>
{
    private const string AdminEmail = RealServer.AdminEmail;
    private const string AdminPassword = RealServer.AdminPassword;
    private const string UserPassword = "correct-horse-battery";

    private readonly ServerProcess _server = realServer.Process;

    private HttpClient Http => new() { BaseAddress = new Uri(_server.BaseUrl) };

    private async Task<BuntingApi> SignIn(string email, string password)
    {
        var http = Http;
        return new BuntingApi(http, new StaticToken(await BuntingApi.LoginAsync(http, email, password)));
    }

    private async Task<(BuntingApi Admin, string Project)> SeedProject()
    {
        var admin = await SignIn(AdminEmail, AdminPassword);
        var project = "p-" + Guid.NewGuid().ToString("N")[..10];
        await admin.CreateProjectAsync(project, "Demo");
        await admin.CreateFlagAsync(
            project,
            new CreateFlagRequest("seed", "Seed", null, FlagType.Boolean, new() { ["on"] = System.Text.Json.JsonSerializer.SerializeToElement(true), ["off"] = System.Text.Json.JsonSerializer.SerializeToElement(false) }, "off", [], false));
        return (admin, project);
    }

    private async Task<BuntingApi> MemberOf(BuntingApi admin, string project, ProjectRole role)
    {
        var email = $"{role}-{Guid.NewGuid():N}"[..20] + "@bunting.test";
        await admin.CreateUserAsync(email, UserPassword, false);
        await admin.AddMemberAsync(project, email, role);
        return await SignIn(email, UserPassword);
    }

    [Fact]
    public async Task A_viewer_can_read_through_the_api_client_but_every_write_is_refused()
    {
        var (admin, project) = await SeedProject();
        var viewer = await MemberOf(admin, project, ProjectRole.Viewer);
        var flag = await viewer.GetFlagAsync(project, "seed");
        var config = await viewer.GetConfigAsync(project, "production", "seed");

        Assert.NotEmpty(await viewer.GetFlagsAsync(project));
        Assert.Equal("seed", flag.Key);

        var writes = new (string Name, Func<Task> Call)[]
        {
            ("create flag", () => viewer.CreateFlagAsync(project, new CreateFlagRequest("x", "x", null, FlagType.Boolean, flag.Variants, "off", [], false))),
            ("update flag", () => viewer.UpdateFlagAsync(project, "seed", new UpdateFlagRequest("y", null, flag.Variants, [], false, false), flag.Version)),
            ("put config", () => viewer.PutConfigAsync(project, "production", "seed", new FlagConfigRequest(true, config.OffVariant, [], config.Fallthrough), config.Version)),
            ("kill switch", () => viewer.SetEnabledAsync(project, "production", "seed", true, config.Version)),
            ("create segment", () => viewer.CreateSegmentAsync(project, "production", new SegmentRequest("s", [], [], []))),
            ("create api key", () => viewer.CreateApiKeyAsync(project, "production", "k", ApiKeyType.Server)),
            ("list api keys", () => viewer.GetApiKeysAsync(project, "production")),
            ("add member", () => viewer.AddMemberAsync(project, AdminEmail, ProjectRole.Viewer)),
            ("create user", () => viewer.CreateUserAsync("x@bunting.test", UserPassword, false)),
            ("create project", () => viewer.CreateProjectAsync("nope-" + Guid.NewGuid().ToString("N")[..6], "x")),
        };
        foreach (var (name, call) in writes)
        {
            var e = await Assert.ThrowsAsync<ApiException>(call);
            Assert.True(e.Status == HttpStatusCode.Forbidden, $"{name}: {e.Status}");
        }

        // Nothing changed.
        Assert.False((await admin.GetConfigAsync(project, "production", "seed")).Enabled);
        Assert.Single(await admin.GetFlagsAsync(project));
    }

    [Fact]
    public async Task An_editor_changes_flags_but_cannot_manage_keys_or_members()
    {
        var (admin, project) = await SeedProject();
        var editor = await MemberOf(admin, project, ProjectRole.Editor);
        var config = await editor.GetConfigAsync(project, "development", "seed");

        var toggled = await editor.SetEnabledAsync(project, "development", "seed", true, config.Version);

        Assert.True(toggled.Enabled);
        Assert.Equal(HttpStatusCode.Forbidden, (await Assert.ThrowsAsync<ApiException>(() => editor.CreateApiKeyAsync(project, "development", "k", ApiKeyType.Server))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Assert.ThrowsAsync<ApiException>(() => editor.GetMembersAsync(project))).Status);
    }

    [Fact]
    public async Task The_playground_endpoint_works_through_the_api_client()
    {
        var (admin, project) = await SeedProject();
        var viewer = await MemberOf(admin, project, ProjectRole.Viewer);

        var response = await viewer.EvaluateAsync(project, "production", "seed", "{\"targetingKey\":\"u1\"}");

        Assert.Single(response.Results);
        Assert.Equal("DISABLED", response.Results[0].Reason);
    }

    [Fact]
    public async Task The_admin_panel_signs_users_in_and_shows_pages_according_to_their_role()
    {
        var (admin, project) = await SeedProject();
        var viewerEmail = $"viewer-{Guid.NewGuid():N}"[..20] + "@bunting.test";
        await admin.CreateUserAsync(viewerEmail, UserPassword, false);
        await admin.AddMemberAsync(project, viewerEmail, ProjectRole.Viewer);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Bunting:ServerUrl", _server.BaseUrl));
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        // Not signed in: everything redirects to the sign-in page.
        var redirect = await anonymous.GetAsync($"/p/{project}");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.StartsWith("/login", Relative(redirect), StringComparison.Ordinal);

        // Wrong password.
        var wrong = await PostLogin(factory, viewerEmail, "wrong-wrong-wrong");
        Assert.Equal("/login?error=credentials", Relative(wrong));

        // The viewer signs in and sees the project, without owner screens.
        using var viewerClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var ok = await PostLogin(factory, viewerEmail, UserPassword, viewerClient);
        Assert.Equal("/", Relative(ok));
        var flags = await viewerClient.GetStringAsync($"/p/{project}");
        Assert.Contains("seed", flags, StringComparison.Ordinal);
        Assert.Contains("Viewer", flags, StringComparison.Ordinal);
        Assert.DoesNotContain("data-testid=\"new-flag\"", flags, StringComparison.Ordinal);
        var members = await viewerClient.GetStringAsync($"/p/{project}/members");
        Assert.Contains("Only project owners manage members", members, StringComparison.Ordinal);

        // The instance administrator gets the owner screens.
        using var adminClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await PostLogin(factory, AdminEmail, AdminPassword, adminClient);
        var ownerFlags = await adminClient.GetStringAsync($"/p/{project}");
        Assert.Contains("data-testid=\"new-flag\"", ownerFlags, StringComparison.Ordinal);
    }

    private static string Relative(HttpResponseMessage response) =>
        response.Headers.Location!.IsAbsoluteUri ? response.Headers.Location.PathAndQuery : response.Headers.Location.OriginalString;

    private static async Task<HttpResponseMessage> PostLogin(WebApplicationFactory<Program> factory, string email, string password, HttpClient? client = null)
    {
        client ??= factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var page = await client.GetAsync("/login");
        var html = await page.Content.ReadAsStringAsync();
        var token = TokenRegex().Match(html).Groups[1].Value;
        Assert.NotEmpty(token);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = token,
        });
        return await client.PostAsync("/account/login", form);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();
}
