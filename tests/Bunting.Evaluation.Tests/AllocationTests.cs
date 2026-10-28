using Bunting.Domain;
using static Bunting.Evaluation.Tests.B;
using Op = Bunting.Domain.ConditionOperator;

namespace Bunting.Evaluation.Tests;

public class AllocationTests
{
    [Fact]
    public void Hot_path_does_not_allocate()
    {
        var rules = Enumerable.Range(0, 19)
            .Select(i => RuleOf($"r{i}", Serve.Single("on"), Cond("plan", Op.Equals, $"plan-{i}"), Cond("country", Op.In, "DE", "FR")))
            .Append(RuleOf("last", Serve.Single("on"), Cond("appVersion", Op.SemVerGreaterThan, "2.0.0"), Cond("age", Op.GreaterThan, "18")))
            .ToArray();
        var flag = Flag(fallthrough: Serve.Split(Roll(("on", 3_000), ("off", 7_000))), rules: rules);
        var ruleset = Compile(flag);
        var context = Ctx("user-12345", ("plan", "free"), ("country", "tr"), ("appVersion", "1.5.0"), ("age", 30));

        for (var i = 0; i < 5_000; i++)
        {
            ruleset.Evaluate("f", context);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            ruleset.Evaluate("f", context);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
