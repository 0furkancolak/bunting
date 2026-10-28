using Bunting.Domain;
using static Bunting.Evaluation.Tests.B;

namespace Bunting.Evaluation.Tests;

public class RolloutTests
{
    private const int Users = 100_000;

    private static List<string> RandomKeys(int count, int seed = 42)
    {
        var rng = new Random(seed);
        var buffer = new byte[16];
        var keys = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            rng.NextBytes(buffer);
            keys.Add(new Guid(buffer).ToString());
        }

        return keys;
    }

    private static CompiledRuleset StringFlag(Rollout rollout, string key = "dist", params string[] names) =>
        Compile(Flag(
            key,
            FlagType.String,
            names.ToDictionary(n => n, n => Json($"\"{n}\"")),
            off: names[0],
            fallthrough: Serve.Split(rollout)));

    [Theory]
    [InlineData(new[] { 5_000, 5_000 })]
    [InlineData(new[] { 2_000, 3_000, 5_000 })]
    [InlineData(new[] { 100, 900, 9_000 })]
    [InlineData(new[] { 3_333, 3_333, 3_334 })]
    [InlineData(new[] { 7_000, 0, 3_000 })]
    public void Distribution_is_within_one_percentage_point_of_target(int[] weights)
    {
        var names = weights.Select((_, i) => $"v{i}").ToArray();
        var rollout = Roll(names.Zip(weights, (n, w) => (n, w)).ToArray());
        var ruleset = StringFlag(rollout, "dist", names);

        var counts = names.ToDictionary(n => n, _ => 0);
        foreach (var key in RandomKeys(Users))
        {
            counts[ruleset.Evaluate("dist", Ctx(key)).Variant!]++;
        }

        for (var i = 0; i < names.Length; i++)
        {
            var actual = 100.0 * counts[names[i]] / Users;
            var target = weights[i] / 100.0;
            Assert.True(Math.Abs(actual - target) <= 1.0, $"{names[i]}: target {target}%, actual {actual:F3}%");
        }
    }

    [Fact]
    public void Same_key_always_gets_the_same_variant()
    {
        var ruleset = StringFlag(Roll(("a", 5_000), ("b", 5_000)), "dist", "a", "b");
        foreach (var key in RandomKeys(1_000, seed: 7))
        {
            var first = ruleset.Evaluate("dist", Ctx(key)).Variant;
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(first, ruleset.Evaluate("dist", Ctx(key)).Variant);
            }
        }
    }

    [Fact]
    public void Growing_the_rollout_never_moves_existing_users()
    {
        var keys = RandomKeys(50_000, seed: 11);
        string[] previous = [];
        foreach (var percent in new[] { 1, 10, 25, 50, 100 })
        {
            var weight = percent * 100;
            var ruleset = StringFlag(Roll(("on", weight), ("off", 10_000 - weight)).WithoutZero(), "sticky", "on", "off");
            var current = keys.Select(k => ruleset.Evaluate("sticky", Ctx(k)).Variant!).ToArray();

            if (previous.Length > 0)
            {
                for (var i = 0; i < keys.Count; i++)
                {
                    if (previous[i] == "on")
                    {
                        Assert.Equal("on", current[i]);
                    }
                }
            }

            previous = current;
        }

        Assert.All(previous, v => Assert.Equal("on", v));
    }

    [Fact]
    public void Zero_percent_serves_nobody_and_hundred_percent_serves_everybody()
    {
        var keys = RandomKeys(5_000, seed: 3);
        var none = StringFlag(Roll(("on", 0), ("off", 10_000)), "z", "on", "off");
        var all = StringFlag(Roll(("on", 10_000), ("off", 0)), "z", "on", "off");
        Assert.All(keys, k => Assert.Equal("off", none.Evaluate("z", Ctx(k)).Variant));
        Assert.All(keys, k => Assert.Equal("on", all.Evaluate("z", Ctx(k)).Variant));
    }

    [Fact]
    public void Salt_and_flag_key_decorrelate_assignments()
    {
        var keys = RandomKeys(20_000, seed: 5);
        var roll = Roll(("on", 5_000), ("off", 5_000));
        var baseline = StringFlag(roll, "one", "on", "off");
        var salted = StringFlag(roll with { Salt = "v2" }, "one", "on", "off");
        var otherFlag = StringFlag(roll, "two", "on", "off");

        double Agreement(CompiledRuleset a, string ka, CompiledRuleset b, string kb) =>
            keys.Count(k => a.Evaluate(ka, Ctx(k)).Variant == b.Evaluate(kb, Ctx(k)).Variant) / (double)keys.Count;

        // Independent 50/50 splits agree about half of the time.
        Assert.InRange(Agreement(baseline, "one", salted, "one"), 0.47, 0.53);
        Assert.InRange(Agreement(baseline, "one", otherFlag, "two"), 0.47, 0.53);
    }

    [Fact]
    public void Bucket_formula_is_murmur3_of_flag_salt_and_value_modulo_10000()
    {
        const string flagKey = "my-flag";
        const string salt = "s1";
        var ruleset = StringFlag(Roll(("a", 5_000), ("b", 5_000)) with { Salt = salt }, flagKey, "a", "b");
        foreach (var key in new[] { "alice", "bob", "ünïcode-ключ", "12345" })
        {
            var bucket = Murmur3.Hash32(System.Text.Encoding.UTF8.GetBytes($"{flagKey}.{salt}.{key}")) % 10_000;
            Assert.Equal(bucket < 5_000 ? "a" : "b", ruleset.Evaluate(flagKey, Ctx(key)).Variant);
        }
    }

    [Fact]
    public void Very_long_keys_are_supported()
    {
        var ruleset = StringFlag(Roll(("a", 5_000), ("b", 5_000)), "dist", "a", "b");
        var key = new string('k', 5_000);
        Assert.False(ruleset.Evaluate("dist", Ctx(key)).IsError);
    }

    [Fact]
    public void Non_string_bucketBy_values_are_stringified_invariantly()
    {
        var rollout = Roll(("a", 5_000), ("b", 5_000)) with { BucketBy = "orgId" };
        var ruleset = StringFlag(rollout, "dist", "a", "b");
        var viaInt = ruleset.Evaluate("dist", Ctx(null, ("orgId", 42))).Variant;
        var viaString = ruleset.Evaluate("dist", Ctx(null, ("orgId", "42"))).Variant;
        Assert.Equal(viaString, viaInt);
    }
}

internal static class RolloutExtensions
{
    /// <summary>Drops zero-weight entries so that a 0% variant listed first stays valid.</summary>
    public static Rollout WithoutZero(this Rollout rollout) =>
        rollout with { Variants = [.. rollout.Variants.Where(v => v.Weight > 0)] };
}
