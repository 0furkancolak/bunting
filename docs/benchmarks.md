# Evaluation benchmarks

`tests/Bunting.Evaluation.Benchmarks` (BenchmarkDotNet). Run with:

```bash
dotnet run -c Release --project tests/Bunting.Evaluation.Benchmarks -- --filter '*'
```

Scenario: one flag with 20 rules (19 two-condition rules on `plan` + `country`, then a rule combining SemVer, numeric and
regex conditions with a rollout), fallthrough rollout, mixed string/int context values.

Apple M4, .NET 10.0.14, Arm64 RyuJIT, 15 iterations after 5 warmups:

| Scenario | Mean | Allocated |
|----------|-----:|----------:|
| 20 rules, last rule matches (rollout) | 792 ns | 0 B |
| 20 rules, no rule matches → fallthrough rollout | 777 ns | 0 B |
| 20 rules, first rule matches | 56 ns | 0 B |
| Unknown flag | 10 ns | 0 B |
| Compile a 20-rule ruleset | 5.3 µs | 18.9 KB |

**Target: < 2 µs for a 20-rule flag — met (worst case ≈ 0.8 µs, 0 bytes allocated).**
Error bars are wide (±~140 ns) because the measurement ran on a laptop with other workloads active; treat the numbers as
an upper bound. `AllocationTests` additionally asserts zero allocation on the hot path in CI.
