using System.Text.Json;
using Bunting.Admin.Api;
using Bunting.Admin.Editing;
using Bunting.Domain;

namespace Bunting.Admin.Tests;

public class EditingTests
{
    private static readonly string[] Variants = ["on", "off"];

    [Fact]
    public void A_rollout_is_valid_only_when_the_weights_total_exactly_100_percent()
    {
        var serve = new ServeVm { IsRollout = true };
        serve.SetVariants(Variants);
        serve.Weights[0].Percent = 30;
        serve.Weights[1].Percent = 60;
        Assert.False(serve.IsValid);
        Assert.Equal(90, serve.TotalPercent);

        serve.Weights[1].Percent = 70;
        Assert.True(serve.IsValid);
    }

    [Fact]
    public void Weights_are_sent_as_basis_points_and_zero_weights_are_left_out()
    {
        var serve = new ServeVm { IsRollout = true };
        serve.SetVariants(Variants);
        serve.Weights[0].Percent = 33.33;
        serve.Weights[1].Percent = 66.67;

        var rollout = serve.ToServe().Rollout!;

        Assert.Equal([3333, 6667], rollout.Variants.Select(v => v.Weight));
        serve.Weights[0].Percent = 0;
        serve.Weights[1].Percent = 100;
        Assert.Single(serve.ToServe().Rollout!.Variants);
    }

    [Fact]
    public void Rollout_settings_survive_a_round_trip()
    {
        var original = Serve.Split(new Rollout([new("on", 2500), new("off", 7500)], "userId", "salt-1"));

        var vm = ServeVm.From(original, Variants);

        Assert.True(vm.IsRollout);
        Assert.Equal(25, vm.Weights.Single(w => w.Variant == "on").Percent);
        Assert.Equal(original.Rollout!.Variants, vm.ToServe().Rollout!.Variants);
        Assert.Equal("userId", vm.ToServe().Rollout!.BucketBy);
        Assert.Equal("salt-1", vm.ToServe().Rollout!.Salt);
    }

    [Fact]
    public void Rules_keep_their_order_and_content_through_the_form_model()
    {
        var rules = new List<Rule>
        {
            new("first", "pro users", [new Condition("plan", ConditionOperator.In, ["pro", "team"], true)], Serve.Single("on")),
            new("second", null, [new Condition("country", ConditionOperator.Equals, ["TR"])], Serve.Single("off")),
        };
        var config = ConfigVm.From(new FlagConfigInfo("f", "production", true, "off", rules, Serve.Single("off"), 7), Variants);

        var request = config.ToRequest();

        Assert.Equal(JsonSerializer.Serialize(rules, BuntingApi.Json), JsonSerializer.Serialize(request.Rules, BuntingApi.Json));
        Assert.Equal(7, config.Version);
    }

    [Fact]
    public void Rules_can_be_reordered_and_the_edges_are_ignored()
    {
        var config = ConfigVm.From(new FlagConfigInfo("f", "dev", true, "off", [], Serve.Single("off"), 1), Variants);
        config.Rules.AddRange([RuleVm.New(Variants, []), RuleVm.New(Variants, ["rule-1"]), RuleVm.New(Variants, ["rule-1", "rule-2"])]);
        var ids = config.Rules.Select(r => r.Id).ToList();

        config.Move(0, 1);
        config.Move(0, -1);
        config.Move(2, 1);

        Assert.Equal([ids[1], ids[0], ids[2]], config.Rules.Select(r => r.Id));
    }

    [Fact]
    public void New_rules_get_unique_ids()
    {
        var a = RuleVm.New(Variants, ["rule-1", "rule-2"]);
        Assert.Equal("rule-3", a.Id);
    }

    [Fact]
    public void A_configuration_is_invalid_with_duplicate_rule_ids_or_a_broken_rollout()
    {
        var config = ConfigVm.From(new FlagConfigInfo("f", "dev", true, "off", [], Serve.Single("off"), 1), Variants);
        Assert.True(config.IsValid);

        config.Rules.Add(RuleVm.New(Variants, []));
        config.Rules.Add(RuleVm.New(Variants, []));
        config.Rules[1].Id = config.Rules[0].Id;
        Assert.False(config.IsValid);

        config.Rules[1].Id = "other";
        Assert.True(config.IsValid);
        config.Fallthrough.IsRollout = true;
        Assert.False(config.IsValid);
    }

    [Fact]
    public void Condition_values_are_split_on_lines_and_commas()
    {
        var vm = new ConditionVm { Attribute = " plan ", Operator = ConditionOperator.In, ValuesText = "pro, team\nenterprise\n\n", Negate = true };

        var condition = vm.ToCondition();

        Assert.Equal("plan", condition.Attribute);
        Assert.Equal(["pro", "team", "enterprise"], condition.Values);
        Assert.True(condition.Negate);
    }

    [Theory]
    [InlineData(FlagType.Boolean, "true", true)]
    [InlineData(FlagType.Boolean, "maybe", false)]
    [InlineData(FlagType.Integer, "42", true)]
    [InlineData(FlagType.Integer, "4.2", false)]
    [InlineData(FlagType.Double, "4.2", true)]
    [InlineData(FlagType.Double, "x", false)]
    [InlineData(FlagType.String, "anything at all", true)]
    [InlineData(FlagType.Object, "{\"a\":1}", true)]
    [InlineData(FlagType.Object, "{nope", false)]
    public void Variant_values_are_parsed_according_to_the_flag_type(FlagType type, string text, bool valid)
    {
        var row = new VariantVm { Key = "k", ValueText = text };

        Assert.Equal(valid, row.TryParse(type, out _) is null);
    }

    [Fact]
    public void The_variant_form_reports_duplicate_and_missing_keys()
    {
        var rows = new List<VariantVm> { new() { Key = "a", ValueText = "1" }, new() { Key = "a", ValueText = "2" }, new() { Key = "", ValueText = "3" } };

        var ok = VariantForm.TryBuild(rows, FlagType.Integer, out _, out var errors);

        Assert.False(ok);
        Assert.Equal([1, 2], errors.Keys.Order());
    }

    [Fact]
    public void Variants_round_trip_through_the_form()
    {
        var original = new Dictionary<string, JsonElement> { ["on"] = JsonSerializer.SerializeToElement(true), ["off"] = JsonSerializer.SerializeToElement(false) };
        var rows = original.Select(v => VariantVm.From(v.Key, v.Value, FlagType.Boolean)).ToList();

        Assert.True(VariantForm.TryBuild(rows, FlagType.Boolean, out var rebuilt, out _));
        Assert.Equal(original.Select(v => v.Value.GetBoolean()), rebuilt.Select(v => v.Value.GetBoolean()));
    }

    [Fact]
    public void The_audit_diff_marks_only_the_changed_lines()
    {
        using var before = JsonDocument.Parse("{\"enabled\":false,\"offVariant\":\"off\"}");
        using var after = JsonDocument.Parse("{\"enabled\":true,\"offVariant\":\"off\"}");

        var diff = AuditDiff.Compare(before.RootElement, after.RootElement);

        Assert.Contains(diff, l => l.Kind == DiffKind.Removed && l.Text.Contains("false", StringComparison.Ordinal));
        Assert.Contains(diff, l => l.Kind == DiffKind.Added && l.Text.Contains("true", StringComparison.Ordinal));
        Assert.Contains(diff, l => l.Kind == DiffKind.Same && l.Text.Contains("offVariant", StringComparison.Ordinal));
    }

    [Fact]
    public void Creating_and_deleting_show_everything_as_added_or_removed()
    {
        using var document = JsonDocument.Parse("{\"key\":\"x\"}");

        Assert.All(AuditDiff.Compare(null, document.RootElement), l => Assert.Equal(DiffKind.Added, l.Kind));
        Assert.All(AuditDiff.Compare(document.RootElement, null), l => Assert.Equal(DiffKind.Removed, l.Kind));
    }

    [Fact]
    public void The_role_of_a_user_comes_from_a_global_role_or_a_membership()
    {
        var member = new MeInfo("a", null, false, null, [new Membership("p1", ProjectRole.Editor)]);
        var admin = new MeInfo("b", null, true, ProjectRole.Owner, []);

        Assert.Equal(ProjectRole.Editor, member.RoleIn("p1"));
        Assert.Null(member.RoleIn("p2"));
        Assert.Equal(ProjectRole.Owner, admin.RoleIn("anything"));
    }
}
