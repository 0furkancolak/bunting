using AngleSharp.Dom;
using Bunting.Admin.Api;
using Bunting.Admin.Components.Pages;
using Bunting.Admin.Components.Shared;
using Bunting.Admin.Tests.Support;
using Bunting.Domain;

namespace Bunting.Admin.Tests;

public class FlagDetailTests
{
    private static async Task<IRenderedComponent<FlagDetail>> Open(AdminTestContext ctx, string flag = "new-checkout")
    {
        var cut = ctx.Render<FlagDetail>(p => p.Add(x => x.Project, FakeApi.Project).Add(x => x.Flag, flag));
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid='environment-editor']")), TimeSpan.FromSeconds(5));
        return cut;
    }

    [Fact]
    public async Task An_editor_can_add_a_rule_and_save_the_environment()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Editor);
        var cut = await Open(ctx);

        cut.Find("[data-testid='add-rule']").Click();
        await cut.WaitForAssertionAsync(() => Assert.Single(cut.FindAll("[data-testid='rule']")));
        cut.Find("[data-testid='save-config']").Click();

        await cut.WaitForAssertionAsync(() => Assert.Contains("put config development/new-checkout", ctx.Api.Writes));
        Assert.Contains("rule-1", ctx.Api.Configs["development/new-checkout"].Rules.Select(r => r.Id));
    }

    [Fact]
    public async Task Rules_can_be_moved_with_the_arrow_buttons()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Editor);
        var cut = await Open(ctx);
        cut.Find("[data-testid='add-rule']").Click();
        await cut.WaitForAssertionAsync(() => Assert.Single(cut.FindAll("[data-testid='rule']")));
        cut.Find("[data-testid='add-rule']").Click();
        await cut.WaitForAssertionAsync(() => Assert.Equal(["rule-1", "rule-2"], cut.FindAll("input[data-testid='rule-id']").Select(i => i.GetAttribute("value"))));

        cut.FindAll("button[aria-label='Move rule down']")[0].Click();

        await cut.WaitForAssertionAsync(() => Assert.Equal(["rule-2", "rule-1"], cut.FindAll("input[data-testid='rule-id']").Select(i => i.GetAttribute("value"))));
    }

    [Fact]
    public async Task Rules_can_be_reordered_by_dragging_a_rule_onto_another()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Editor);
        var cut = await Open(ctx);
        for (var i = 1; i <= 3; i++)
        {
            cut.Find("[data-testid='add-rule']").Click();
            var expected = i;
            await cut.WaitForAssertionAsync(() => Assert.Equal(expected, cut.FindAll("[data-testid='rule']").Count));
        }

        cut.FindAll("[data-testid='rule-handle']")[0].DragStart();
        cut.FindAll("[data-testid='rule']")[2].Drop();

        Assert.Equal(["rule-2", "rule-3", "rule-1"], cut.FindAll("input[data-testid='rule-id']").Select(i => i.GetAttribute("value")));
    }

    [Fact]
    public async Task Removing_a_rule_takes_it_out_of_the_list()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Editor);
        var cut = await Open(ctx);
        cut.Find("[data-testid='add-rule']").Click();
        await cut.WaitForAssertionAsync(() => Assert.Single(cut.FindAll("[data-testid='rule']")));

        cut.Find("button[aria-label='Remove rule']").Click();

        await cut.WaitForAssertionAsync(() => Assert.Empty(cut.FindAll("[data-testid='rule']")));
    }

    [Fact]
    public async Task Conditions_can_be_added_to_a_rule()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Editor);
        var cut = await Open(ctx);
        cut.Find("[data-testid='add-rule']").Click();
        await cut.WaitForAssertionAsync(() => Assert.Single(cut.FindAll("[data-testid='condition']")));

        cut.Find("[data-testid='add-condition']").Click();

        await cut.WaitForAssertionAsync(() => Assert.Equal(2, cut.FindAll("[data-testid='condition']").Count));
    }

    [Fact]
    public async Task A_rollout_that_does_not_total_100_percent_blocks_saving()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Editor);
        var cut = await Open(ctx);
        var serve = cut.FindComponents<ServeEditor>().Single();
        serve.FindAll("input[type='radio']")[1].Click();
        await serve.WaitForAssertionAsync(() => Assert.NotEmpty(serve.FindAll("[data-testid='rollout']")));

        Assert.Contains("must be exactly 100%", cut.Find("[data-testid='rollout-total']").TextContent);
        Assert.True(cut.Find("[data-testid='save-config']").HasAttribute("disabled"));
        Assert.NotEmpty(cut.FindAll("[data-testid='config-invalid']"));

        var inputs = serve.FindAll("[data-testid='rollout'] input[type='text'], [data-testid='rollout'] input[inputmode='decimal'], [data-testid='rollout'] input.mud-input-slot");
        inputs[0].Change("30");
        inputs[1].Change("70");

        await cut.WaitForAssertionAsync(() => Assert.False(cut.Find("[data-testid='save-config']").HasAttribute("disabled")));
        Assert.DoesNotContain("must be exactly", cut.Find("[data-testid='rollout-total']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_a_production_configuration_asks_for_confirmation()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Editor);
        var cut = await Open(ctx);
        cut.FindAll("[role='tab']").Single(t => t.TextContent.Contains("production", StringComparison.OrdinalIgnoreCase)).Click();
        await cut.WaitForAssertionAsync(() => Assert.Contains("Save production", cut.Markup));

        cut.Find("[data-testid='save-config']").Click();

        await ctx.Dialogs.WaitForAssertionAsync(() => Assert.Contains("Save to production?", ctx.Dialogs.Markup), TimeSpan.FromSeconds(5));
        Assert.Empty(ctx.Api.Writes);
        ctx.Dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Save").Click();
        await cut.WaitForAssertionAsync(() => Assert.Contains("put config production/new-checkout", ctx.Api.Writes));
    }

    [Fact]
    public async Task A_viewer_sees_the_configuration_but_cannot_change_it()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Viewer);
        var cut = await Open(ctx);

        Assert.Empty(cut.FindAll("[data-testid='add-rule']"));
        Assert.Empty(cut.FindAll("[data-testid='save-config']"));
        Assert.Empty(cut.FindAll("[data-testid='save-flag']"));
        Assert.NotEmpty(cut.FindAll("[data-testid='read-only']"));
        Assert.All(cut.FindAll("[data-testid='enabled']"), e => Assert.True(e.HasAttribute("disabled")));
        Assert.Empty(ctx.Api.Writes);
    }

    [Fact]
    public async Task Creating_a_flag_validates_variants_and_calls_the_api()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Editor);
        var cut = ctx.Render<FlagCreate>(p => p.Add(x => x.Project, FakeApi.Project));
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("input[data-testid='flag-key']")));
        Assert.True(cut.Find("[data-testid='create-flag']").HasAttribute("disabled"));

        cut.Find("input[data-testid='flag-key']").Input("shiny-new");
        cut.Find("input[data-testid='flag-name']").Input("Shiny");
        cut.Find("[data-testid='create-flag']").Click();

        await cut.WaitForAssertionAsync(() => Assert.Contains("create flag shiny-new", ctx.Api.Writes));
    }

    [Fact]
    public async Task A_viewer_cannot_create_flags()
    {
        await using var ctx = new AdminTestContext(ProjectRole.Viewer);
        var cut = ctx.Render<FlagCreate>(p => p.Add(x => x.Project, FakeApi.Project));

        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid='read-only']")));
        Assert.Empty(cut.FindAll("[data-testid='create-flag']"));
    }
}
