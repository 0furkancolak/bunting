# ADR 0006: Compile rulesets once; evaluation is allocation-free

- Status: Accepted

## Context
Evaluation runs on request hot paths in the SDK and the server. The target is < 2 µs for a 20-rule flag with minimal
allocation, and no JSON parsing per evaluation.

## Decision
- A `RulesetSnapshot` (plain records, JSON-serialisable) is compiled into an immutable `CompiledRuleset`: operands are
  pre-parsed (numbers, SemVer, dates), regexes are built once (50 ms timeout, process-wide cache so ruleset swaps do not
  recompile), segment references are resolved to objects, rollouts get a precomputed UTF-8 hash prefix and cumulative
  weights, and variant values are kept as `JsonElement`s that are returned by reference.
- `EvaluationResult` is a readonly struct; contexts are read through the dictionary interface.
- Compilation never throws on bad content; it produces diagnostics and neutralises only what is broken (see
  `evaluation-semantics.md`).
- Compiled rulesets are immutable, so the SDK can swap them with a single reference assignment and evaluate without locks.

## Consequences
- Operand parsing errors surface at publish/compile time, not at request time.
- `JsonElement` values are valid for the lifetime of the ruleset's documents; snapshots must be built from cloned
  elements (`JsonElement.Clone()`), as done by the server and SDK deserialisers.
- An allocation test (`AllocationTests`) guards the zero-allocation hot path.
