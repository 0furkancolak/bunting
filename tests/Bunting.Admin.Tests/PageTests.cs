using System.Text.Json;
using Bunting.Admin.Api;
using Bunting.Admin.Components.Pages;
using Bunting.Admin.Tests.Support;
using Bunting.Domain;

namespace Bunting.Admin.Tests;

public class PageTests
{
    [Fact]
    public async Task The_project_list_shows_the_projects_of_the_user()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Viewer);

        var cut = ctx.Render<Projects>();

        await cut.WaitForAssertionAsync(() => Assert.Single(cut.FindAll("[data-testid='project-card']")));
        Assert.Contains("Demo shop", cut.Markup);
        Assert.Empty(cut.FindAll("[data-testid='new-project']"));
    }

    [Fact]
    public async Task Only_instance_administrators_can_create_projects()
    {
        await using var ctx = new AdminTestContext(role: null, instanceAdmin: true);

        var cut = ctx.Render<Projects>();

        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid='new-project']")));
    }

    [Fact]
    public async Task The_segment_editor_creates_a_segment_with_conditions()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Editor);
        var cut = ctx.Render<Segments>(p => p.Add(x => x.Project, FakeApi.Project));
        await cut.WaitForAssertionAsync(() => Assert.Contains("beta-users", cut.Markup));

        cut.Find("[data-testid='new-segment']").Click();
        cut.Find("input[data-testid='segment-key']").Input("employees");
        cut.Find("[data-testid='save-segment']").Click();

        await cut.WaitForAssertionAsync(() => Assert.Contains("create segment employees", ctx.Api.Writes));
    }

    [Fact]
    public async Task A_viewer_can_look_at_segments_but_not_change_them()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Viewer);
        var cut = ctx.Render<Segments>(p => p.Add(x => x.Project, FakeApi.Project));
        await cut.WaitForAssertionAsync(() => Assert.Contains("beta-users", cut.Markup));

        Assert.Empty(cut.FindAll("[data-testid='new-segment']"));
        Assert.DoesNotContain("Delete", cut.Find("[data-testid='segment-table']").TextContent, StringComparison.Ordinal);
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "View").Click();
        Assert.Empty(cut.FindAll("[data-testid='save-segment']"));
        Assert.Empty(ctx.Api.Writes);
    }

    [Fact]
    public async Task The_playground_sends_the_context_and_shows_reason_variant_and_rule()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Viewer);
        ctx.Api.PlaygroundAnswer = new PlaygroundResponse(
            7,
            [new PlaygroundResult("new-checkout", JsonSerializer.SerializeToElement(true), "on", "TARGETING_MATCH", null, "pro-users"), new PlaygroundResult("missing", null, null, "ERROR", "FLAG_NOT_FOUND", null)]);
        var cut = ctx.Render<Playground>(p => p.Add(x => x.Project, FakeApi.Project));
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid='playground-run']")));

        cut.Find("textarea[data-testid='playground-context']").Input("{\"targetingKey\":\"u1\",\"plan\":\"pro\"}");
        cut.Find("[data-testid='playground-run']").Click();

        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid='playground-results']")));
        var table = cut.Find("[data-testid='playground-results']").TextContent;
        Assert.Contains("TARGETING_MATCH", table, StringComparison.Ordinal);
        Assert.Contains("pro-users", table, StringComparison.Ordinal);
        Assert.Contains("FLAG_NOT_FOUND", table, StringComparison.Ordinal);
        Assert.Contains("\"plan\":\"pro\"", ctx.Api.LastPlaygroundContext, StringComparison.Ordinal);
        Assert.Empty(ctx.Api.Writes);
    }

    [Fact]
    public async Task The_playground_refuses_invalid_json_before_calling_the_server()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Viewer);
        var cut = ctx.Render<Playground>(p => p.Add(x => x.Project, FakeApi.Project));
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid='playground-run']")));

        cut.Find("textarea[data-testid='playground-context']").Input("{ not json");

        Assert.True(cut.Find("[data-testid='playground-run']").HasAttribute("disabled"));
        Assert.Null(ctx.Api.LastPlaygroundContext);
    }

    [Fact]
    public async Task The_audit_log_shows_a_before_after_diff_on_demand()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Viewer);
        using var before = JsonDocument.Parse("{\"enabled\":false}");
        using var after = JsonDocument.Parse("{\"enabled\":true}");
        ctx.Api.Audit.Add(new AuditItem(Guid.NewGuid(), DateTimeOffset.UtcNow, "alice@example.com", "flag-config", "new-checkout", "updated", "production", before.RootElement.Clone(), after.RootElement.Clone()));
        var cut = ctx.Render<Audit>(p => p.Add(x => x.Project, FakeApi.Project));
        await cut.WaitForAssertionAsync(() => Assert.Contains("alice@example.com", cut.Markup));
        Assert.Empty(cut.FindAll("[data-testid='audit-diff']"));

        cut.Find("[data-testid='audit-toggle']").Click();

        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid='audit-diff']")));
        Assert.NotEmpty(cut.FindAll(".diff-added"));
        Assert.NotEmpty(cut.FindAll(".diff-removed"));
    }

    [Fact]
    public async Task An_owner_creates_an_api_key_and_sees_the_secret_once()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Owner);
        var cut = ctx.Render<ApiKeys>(p => p.Add(x => x.Project, FakeApi.Project));
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid='key-name']")));

        cut.Find("input[data-testid='key-name']").Input("mobile");
        cut.Find("[data-testid='create-key']").Click();

        await cut.WaitForAssertionAsync(() => Assert.Contains("bnt_srv_plaintext_once", cut.Find("[data-testid='created-key-value']").TextContent, StringComparison.Ordinal));
        Assert.Contains("create key mobile", ctx.Api.Writes);
    }

    [Theory]
    [InlineData(ProjectRole.Viewer)]
    [InlineData(ProjectRole.Editor)]
    public async Task Only_owners_get_the_key_and_member_management_screens(ProjectRole role)
    {
        await using var ctx = new AdminTestContext(role);
        var keys = ctx.Render<ApiKeys>(p => p.Add(x => x.Project, FakeApi.Project));
        var members = ctx.Render<Members>(p => p.Add(x => x.Project, FakeApi.Project));
        var users = ctx.Render<Users>();

        await keys.WaitForAssertionAsync(() => Assert.NotEmpty(keys.FindAll("[data-testid='project-role']")));
        await members.WaitForAssertionAsync(() => Assert.NotEmpty(members.FindAll("[data-testid='project-role']")));
        await users.WaitForAssertionAsync(() => Assert.NotEmpty(users.FindAll("[data-testid='read-only']")));

        // Owners' pages show the form; for the others the shell shows no navigation entry and the panels refuse.
        Assert.DoesNotContain("API keys", keys.Find("[data-testid='project-nav']").TextContent, StringComparison.Ordinal);
        Assert.Empty(keys.FindAll("[data-testid='create-key']"));
        Assert.Empty(members.FindAll("[data-testid='add-member']"));
        Assert.Empty(users.FindAll("[data-testid='create-user']"));
        Assert.Empty(ctx.Api.Writes);
    }

    [Fact]
    public async Task An_owner_manages_members()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Owner);
        var cut = ctx.Render<Members>(p => p.Add(x => x.Project, FakeApi.Project));
        await cut.WaitForAssertionAsync(() => Assert.Contains("alice@example.com", cut.Markup));

        cut.Find("input[data-testid='member-email']").Input("bob@example.com");
        cut.Find("[data-testid='add-member']").Click();

        await cut.WaitForAssertionAsync(() => Assert.Contains("add member bob@example.com", ctx.Api.Writes));
    }

    [Fact]
    public async Task An_instance_administrator_manages_users()
    {
        await using var ctx = new AdminTestContext(role: null, instanceAdmin: true);
        var cut = ctx.Render<Users>();
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid='create-user']")));

        cut.Find("input[data-testid='user-email']").Input("carol@example.com");
        cut.Find("input[type='password']").Input("a-long-password");
        cut.Find("[data-testid='create-user']").Click();

        await cut.WaitForAssertionAsync(() => Assert.Contains("create user carol@example.com", ctx.Api.Writes));
    }

    /// <summary>The acceptance check for roles in the UI: a viewer opens every screen and no write control is offered.</summary>
    [Fact]
    public async Task A_viewer_is_offered_no_write_control_on_any_screen()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Viewer);
        var screens = new List<IRenderedComponent<Microsoft.AspNetCore.Components.IComponent>>();
        string[] writeTestIds = ["new-flag", "save-flag", "save-config", "add-rule", "add-condition", "create-flag", "new-segment", "save-segment", "create-key", "revoke-key", "add-member", "create-user", "new-project"];
        string[] writeLabels = ["Save", "Create", "Add ", "Delete", "Revoke", "Remove", "New "];
        var pages = new List<Func<IRenderedComponent<Microsoft.AspNetCore.Components.IComponent>>>
        {
            () => ctx.Render<Projects>(),
            () => ctx.Render<FlagList>(p => p.Add(x => x.Project, FakeApi.Project)),
            () => ctx.Render<FlagCreate>(p => p.Add(x => x.Project, FakeApi.Project)),
            () => ctx.Render<FlagDetail>(p => p.Add(x => x.Project, FakeApi.Project).Add(x => x.Flag, "new-checkout")),
            () => ctx.Render<Segments>(p => p.Add(x => x.Project, FakeApi.Project)),
            () => ctx.Render<Playground>(p => p.Add(x => x.Project, FakeApi.Project)),
            () => ctx.Render<Audit>(p => p.Add(x => x.Project, FakeApi.Project)),
            () => ctx.Render<ApiKeys>(p => p.Add(x => x.Project, FakeApi.Project)),
            () => ctx.Render<Members>(p => p.Add(x => x.Project, FakeApi.Project)),
            () => ctx.Render<Users>(),
        };
        foreach (var render in pages)
        {
            var cut = render();
            await Task.Delay(300);
            cut.Render();
            Assert.DoesNotContain("mud-progress-linear", cut.Markup, StringComparison.Ordinal);
            foreach (var id in writeTestIds)
            {
                Assert.Empty(cut.FindAll($"[data-testid='{id}']"));
            }

            Assert.DoesNotContain(
                cut.FindAll("button").Where(b => !b.HasAttribute("disabled")),
                b => writeLabels.Any(l => b.TextContent.Trim().StartsWith(l, StringComparison.Ordinal)));
            screens.Add(cut);
        }

        Assert.Empty(ctx.Api.Writes);
    }
}
