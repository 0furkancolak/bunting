using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Bunting.Domain;
using Bunting.Evaluation;

namespace Bunting.Evaluation.Benchmarks;

/// <summary>
/// Target: a flag with 20 rules evaluates in &lt; 2 µs with minimal (ideally zero) allocation.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 15)]
public class EvaluationBenchmarks
{
    private CompiledRuleset _ruleset = null!;
    private EvaluationContext _noMatch = null!;
    private EvaluationContext _lastRuleMatch = null!;
    private EvaluationContext _firstRuleMatch = null!;
    private RulesetSnapshot _snapshot = null!;

    [GlobalSetup]
    public void Setup()
    {
        var variants = new Dictionary<string, JsonElement>
        {
            ["on"] = JsonDocument.Parse("true").RootElement,
            ["off"] = JsonDocument.Parse("false").RootElement,
        };

        // 19 rules that need two conditions each, then a final rule using SemVer + numeric + regex.
        var rules = new List<Rule>();
        for (var i = 0; i < 19; i++)
        {
            rules.Add(new Rule(
                $"r{i}",
                null,
                [
                    new Condition("plan", ConditionOperator.Equals, [$"plan-{i}"]),
                    new Condition("country", ConditionOperator.In, ["DE", "FR", "ES"]),
                ],
                Serve.Single("on")));
        }

        rules.Add(new Rule(
            "last",
            null,
            [
                new Condition("appVersion", ConditionOperator.SemVerGreaterThan, ["2.0.0"]),
                new Condition("age", ConditionOperator.GreaterThan, ["18"]),
                new Condition("email", ConditionOperator.Matches, [@"@corp\.example$"]),
            ],
            Serve.Split(new Rollout([new WeightedVariant("on", 2_500), new WeightedVariant("off", 7_500)]))));

        // Mixed context types: a realistic hot path rather than a single comparison.
        var flag = new FlagSnapshot(
            "checkout-v2", FlagType.Boolean, variants, Enabled: true, Archived: false, OffVariant: "off", rules,
            Serve.Split(new Rollout([new WeightedVariant("on", 5_000), new WeightedVariant("off", 5_000)])));

        _snapshot = new RulesetSnapshot(1, [flag], []);
        _ruleset = CompiledRuleset.Compile(_snapshot);

        _noMatch = new EvaluationContext("user-12345", new Dictionary<string, object?>
        {
            ["plan"] = "free",
            ["country"] = "TR",
            ["appVersion"] = "1.5.0",
            ["age"] = 30,
            ["email"] = "a@b.c",
        });
        _lastRuleMatch = new EvaluationContext("user-12345", new Dictionary<string, object?>
        {
            ["plan"] = "free",
            ["country"] = "TR",
            ["appVersion"] = "3.1.0",
            ["age"] = 30,
            ["email"] = "me@corp.example",
        });
        _firstRuleMatch = new EvaluationContext("user-12345", new Dictionary<string, object?>
        {
            ["plan"] = "plan-0",
            ["country"] = "de",
        });
    }

    [Benchmark(Baseline = true, Description = "20 rules, last rule matches (rollout)")]
    public EvaluationResult LastRuleMatches() => _ruleset.Evaluate("checkout-v2", _lastRuleMatch, FlagType.Boolean);

    [Benchmark(Description = "20 rules, none match -> fallthrough rollout")]
    public EvaluationResult NoRuleMatches() => _ruleset.Evaluate("checkout-v2", _noMatch, FlagType.Boolean);

    [Benchmark(Description = "20 rules, first rule matches")]
    public EvaluationResult FirstRuleMatches() => _ruleset.Evaluate("checkout-v2", _firstRuleMatch, FlagType.Boolean);

    [Benchmark(Description = "Unknown flag")]
    public EvaluationResult FlagNotFound() => _ruleset.Evaluate("nope", _noMatch);

    [Benchmark(Description = "Compile ruleset (20 rules)")]
    public CompiledRuleset Compile() => CompiledRuleset.Compile(_snapshot);
}
