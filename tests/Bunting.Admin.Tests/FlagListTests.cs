using AngleSharp.Dom;
using Bunting.Admin.Components.Pages;
using Bunting.Admin.Components.Shared;
using Bunting.Admin.Tests.Support;
using Bunting.Domain;

namespace Bunting.Admin.Tests;

public class FlagListTests
{
    private static IElement Toggle(IRenderedComponent<FlagList> cut, string flag, string environment) =>
        cut.Find($"input[aria-label='{flag} in {environment}']");

    [Fact]
    public async Task The_list_shows_flags_with_a_state_per_environment()
    {
        await using var ctx = new AdminTestContext();

        var cut = ctx.Render<FlagList>(p => p.Add(x => x.Project, FakeApi.Project));
        await cut.WaitForAssertionAsync(() => Assert.Contains("new-checkout", cut.Markup));

        Assert.Contains("banner-text", cut.Markup);
        Assert.False(Toggle(cut, "new-checkout", "production").HasAttribute("disabled"));
        Assert.True(Toggle(cut, "new-checkout", "production").HasAttribute("checked") || Toggle(cut, "new-checkout", "production").GetAttribute("checked") is not null);
        Assert.NotNull(cut.Find("[data-testid='new-flag']"));
    }

    [Fact]
    public async Task Searching_filters_the_list()
    {
        await using var ctx = new AdminTestContext();
        var cut = ctx.Render<FlagList>(p => p.Add(x => x.Project, FakeApi.Project));
        await cut.WaitForAssertionAsync(() => Assert.Contains("banner-text", cut.Markup));

        cut.Find("input[data-testid='flag-search']").Input("checkout");

        await cut.WaitForAssertionAsync(() => Assert.DoesNotContain("banner-text", cut.Markup), TimeSpan.FromSeconds(3));
        Assert.Contains("new-checkout", cut.Markup);
    }

    [Fact]
    public async Task Switching_a_non_production_flag_calls_the_api_right_away()
    {
        await using var ctx = new AdminTestContext();
        var cut = ctx.Render<FlagList>(p => p.Add(x => x.Project, FakeApi.Project));
        await cut.WaitForAssertionAsync(() => Assert.Contains("new-checkout", cut.Markup));

        Toggle(cut, "new-checkout", "development").Change(true);

        await cut.WaitForAssertionAsync(() => Assert.Contains("set enabled development/new-checkout=True", ctx.Api.Writes));
    }

    [Fact]
    public async Task Switching_a_production_flag_asks_for_confirmation_first()
    {
        await using var ctx = new AdminTestContext();
        var cut = ctx.Render<FlagList>(p => p.Add(x => x.Project, FakeApi.Project));
        await cut.WaitForAssertionAsync(() => Assert.Contains("new-checkout", cut.Markup));
        var dialogs = ctx.Dialogs;

        Toggle(cut, "new-checkout", "production").Change(false);

        await dialogs.WaitForAssertionAsync(() => Assert.Contains("Turn off in production?", dialogs.Markup), TimeSpan.FromSeconds(5));
        Assert.Empty(ctx.Api.Writes);

        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        await dialogs.WaitForAssertionAsync(() => Assert.DoesNotContain("Turn off in production?", dialogs.Markup));
        Assert.Empty(ctx.Api.Writes);

        Toggle(cut, "new-checkout", "production").Change(false);
        await dialogs.WaitForAssertionAsync(() => Assert.Contains("Turn off in production?", dialogs.Markup), TimeSpan.FromSeconds(5));
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Turn off").Click();

        await cut.WaitForAssertionAsync(() => Assert.Contains("set enabled production/new-checkout=False", ctx.Api.Writes));
    }

    [Fact]
    public async Task A_viewer_gets_no_switches_to_flip_and_no_create_button()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Viewer);
        var cut = ctx.Render<FlagList>(p => p.Add(x => x.Project, FakeApi.Project));
        await cut.WaitForAssertionAsync(() => Assert.Contains("new-checkout", cut.Markup));

        Assert.All(cut.FindAll("input[type='checkbox'][aria-label*=' in ']"), toggle => Assert.True(toggle.HasAttribute("disabled")));
        Assert.Empty(cut.FindAll("[data-testid='new-flag']"));
        Assert.Empty(ctx.Api.Writes);
    }

    [Fact]
    public async Task A_user_without_any_role_in_the_project_is_told_so()
    {
        await using var ctx = new AdminTestContext(role: null);
        ctx.Api.Role = null;

        var cut = ctx.Render<FlagList>(p => p.Add(x => x.Project, FakeApi.Project));

        await cut.WaitForAssertionAsync(() => Assert.Contains("No role", cut.Markup));
        Assert.Empty(cut.FindAll("[data-testid='new-flag']"));
    }

    [Fact]
    public async Task The_project_navigation_shows_owner_pages_only_to_owners()
    {
        await using var owner = new AdminTestContext(ProjectRole.Owner);
        await using var editor = new AdminTestContext(ProjectRole.Editor);

        var ownerPage = owner.Render<FlagList>(p => p.Add(x => x.Project, FakeApi.Project));
        var editorPage = editor.Render<FlagList>(p => p.Add(x => x.Project, FakeApi.Project));
        await ownerPage.WaitForAssertionAsync(() => Assert.Contains("project-nav", ownerPage.Markup));
        await editorPage.WaitForAssertionAsync(() => Assert.Contains("project-nav", editorPage.Markup));

        Assert.Contains("API keys", ownerPage.Find("[data-testid='project-nav']").TextContent);
        Assert.Contains("Members", ownerPage.Find("[data-testid='project-nav']").TextContent);
        Assert.DoesNotContain("API keys", editorPage.Find("[data-testid='project-nav']").TextContent);
        Assert.DoesNotContain("Members", editorPage.Find("[data-testid='project-nav']").TextContent);
    }
}
