using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bunting.Server.Tests;

[Collection("server")]
public class IdentityTests(ServerFixture fixture)
{
    [Fact]
    public async Task Login_returns_a_token_that_authenticates_management_calls()
    {
        var (email, _) = await fixture.NewUser();
        var api = await fixture.LoginAsync(email, ServerFixture.UserPassword);

        var me = await Api.Body(await api.Get("/api/v1/auth/me"));

        Assert.Equal(email, me.GetProperty("name").GetString());
        Assert.False(me.GetProperty("isInstanceAdmin").GetBoolean());
        Assert.Empty(me.GetProperty("memberships").EnumerateArray());
    }

    [Fact]
    public async Task Wrong_credentials_are_rejected_without_revealing_which_part_was_wrong()
    {
        var (email, _) = await fixture.NewUser();
        var http = fixture.Factory.CreateClient();

        var wrongPassword = await http.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "nope-nope-nope" });
        var unknownUser = await http.PostAsJsonAsync("/api/v1/auth/login", new { email = "nobody@bunting.test", password = "nope-nope-nope" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(
            (await Api.Body(wrongPassword)).GetProperty("detail").GetString(),
            (await Api.Body(unknownUser)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_even_for_the_right_password()
    {
        var (email, _) = await fixture.NewUser();
        var http = fixture.Factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            await http.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "wrong-wrong-wrong" });
        }

        var locked = await http.PostAsJsonAsync("/api/v1/auth/login", new { email, password = ServerFixture.UserPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
    }

    [Fact]
    public async Task The_bootstrap_administrator_exists_and_is_an_instance_admin()
    {
        var api = await fixture.LoginAsync(ServerFixture.BootstrapEmail, ServerFixture.BootstrapPassword);

        var me = await Api.Body(await api.Get("/api/v1/auth/me"));

        Assert.True(me.GetProperty("isInstanceAdmin").GetBoolean());
    }

    [Fact]
    public async Task Weak_passwords_and_duplicate_emails_are_refused()
    {
        var owner = fixture.Client();
        var (email, _) = await fixture.NewUser();

        var weak = await owner.Post("/api/v1/users", new { email = "weak@bunting.test", password = "short" });
        var duplicate = await owner.Post("/api/v1/users", new { email, password = ServerFixture.UserPassword });

        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }

    [Fact]
    public async Task Only_instance_administrators_manage_users_and_create_projects()
    {
        var project = await fixture.Client().NewProject();
        var projectOwner = await fixture.MemberClient(project, "Owner");
        var admin = await fixture.LoginAsync(ServerFixture.BootstrapEmail, ServerFixture.BootstrapPassword);

        Assert.Equal(HttpStatusCode.Forbidden, (await projectOwner.Get("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await projectOwner.Post("/api/v1/projects", new { key = "x-" + Guid.NewGuid().ToString("N")[..8], name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Get("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.Post("/api/v1/projects", new { key = "x-" + Guid.NewGuid().ToString("N")[..8], name = "x" })).StatusCode);
    }

    [Fact]
    public async Task Project_roles_apply_only_to_their_own_project()
    {
        var owner = fixture.Client();
        var projectA = await owner.NewProject();
        var projectB = await owner.NewProject();
        await owner.NewBoolFlag(projectA, "seed");
        await owner.NewBoolFlag(projectB, "seed");
        var editorOfA = await fixture.MemberClient(projectA, "Editor");

        var url = (string p) => $"/api/v1/projects/{p}/environments/production/flags/seed";
        var own = await editorOfA.Patch(url(projectA), new { enabled = true }, Api.ETag(await editorOfA.Get(url(projectA))));
        var other = await editorOfA.Get(url(projectB));

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Fact]
    public async Task Listing_projects_shows_only_the_projects_of_a_member()
    {
        var owner = fixture.Client();
        var mine = await owner.NewProject();
        var theirs = await owner.NewProject();
        var viewer = await fixture.MemberClient(mine, "Viewer");

        var keys = (await Api.Body(await viewer.Get("/api/v1/projects"))).EnumerateArray().Select(p => p.GetProperty("key").GetString()).ToList();

        Assert.Contains(mine, keys);
        Assert.DoesNotContain(theirs, keys);
        var me = await Api.Body(await viewer.Get("/api/v1/auth/me"));
        Assert.Equal(mine, me.GetProperty("memberships")[0].GetProperty("projectKey").GetString());
    }

    [Fact]
    public async Task Member_management_requires_project_ownership_and_is_audited()
    {
        var owner = fixture.Client();
        var project = await owner.NewProject();
        var projectOwner = await fixture.MemberClient(project, "Owner");
        var editor = await fixture.MemberClient(project, "Editor");
        var (email, userId) = await fixture.NewUser();

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.Post($"/api/v1/projects/{project}/members", new { email, role = "Viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await projectOwner.Post($"/api/v1/projects/{project}/members", new { email, role = "Viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await projectOwner.Post($"/api/v1/projects/{project}/members", new { email, role = "Editor" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await projectOwner.Put($"/api/v1/projects/{project}/members/{userId}", new { role = "Editor" }, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await projectOwner.Delete($"/api/v1/projects/{project}/members/{userId}")).StatusCode);

        var audit = await Api.Body(await owner.Get($"/api/v1/projects/{project}/audit?entityType=member"));
        var actions = audit.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()).ToList();
        Assert.Contains("added", actions);
        Assert.Contains("updated", actions);
        Assert.Contains("removed", actions);
    }

    [Fact]
    public async Task Audit_entries_name_the_user_who_made_the_change()
    {
        var owner = fixture.Client();
        var project = await owner.NewProject();
        var editor = await fixture.MemberClient(project, "Editor");
        await editor.Post($"/api/v1/projects/{project}/flags", new { key = "by-user", name = "x", type = "Boolean", variants = new { on = true, off = false }, defaultVariant = "off" });

        var audit = await Api.Body(await owner.Get($"/api/v1/projects/{project}/audit?entityId=by-user"));

        var me = await Api.Body(await editor.Get("/api/v1/auth/me"));
        Assert.Equal(me.GetProperty("name").GetString(), audit.GetProperty("items")[0].GetProperty("actor").GetString());
    }

    [Fact]
    public async Task The_playground_evaluates_with_the_real_engine_and_is_available_to_viewers()
    {
        var owner = fixture.Client();
        var project = await owner.NewProject();
        await owner.NewBoolFlag(project, "promo");
        var url = $"/api/v1/projects/{project}/environments/production/flags/promo";
        await owner.Put(
            url,
            new
            {
                enabled = true,
                offVariant = "off",
                rules = new[] { new { id = "pro", conditions = new[] { new { attribute = "plan", @operator = "Equals", values = new[] { "pro" } } }, serve = new { variant = "on" } } },
                fallthrough = new { variant = "off" },
            },
            Api.ETag(await owner.Get(url)));
        var viewer = await fixture.MemberClient(project, "Viewer");

        var response = await viewer.Post(
            $"/api/v1/projects/{project}/environments/production/evaluate",
            new { flagKey = "promo", context = new { targetingKey = "u1", plan = "pro" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await Api.Body(response)).GetProperty("results")[0];
        Assert.True(result.GetProperty("value").GetBoolean());
        Assert.Equal("on", result.GetProperty("variant").GetString());
        Assert.Equal("TARGETING_MATCH", result.GetProperty("reason").GetString());
        Assert.Equal("pro", result.GetProperty("ruleId").GetString());
    }

    [Fact]
    public async Task The_playground_reports_errors_per_flag_and_rejects_bad_contexts()
    {
        var owner = fixture.Client();
        var project = await owner.NewProject();
        var url = $"/api/v1/projects/{project}/environments/production/evaluate";

        var missing = await Api.Body(await owner.Post(url, new { flagKey = "nope", context = new { } }));
        var bad = await owner.Post(url, new { context = new { nested = new { a = 1 } } });

        Assert.Equal("FLAG_NOT_FOUND", missing.GetProperty("results")[0].GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Non_members_cannot_use_the_playground()
    {
        var project = await fixture.Client().NewProject();
        var (email, _) = await fixture.NewUser();
        var stranger = await fixture.LoginAsync(email, ServerFixture.UserPassword);

        var response = await stranger.Post($"/api/v1/projects/{project}/environments/production/evaluate", new { context = new { } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
