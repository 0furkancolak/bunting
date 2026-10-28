# ADR 0004: Conditions on missing attributes never match, even when negated

- Status: Accepted

## Context
A targeting rule such as `plan != "pro"` can be read two ways when the context has no `plan` attribute:
"the user is not on pro" (true) or "we do not know the plan" (unknown). Treating unknown as true makes negated
rules fire for every client that simply forgot to send an attribute, which is dangerous for a rule like
`country != "US" → enable`: an unauthenticated or partially populated request would silently get the feature.

## Decision
A condition is *evaluable* only if the attribute is present and has a type the operator understands. Non-evaluable
conditions never match, regardless of `Negate`. `Negate` inverts only evaluable results. `InSegment` is the exception:
membership is always decidable, so "not in segment" works without any attribute.

## Consequences
- Negated rules are safe by default; "everyone except X" must be expressed as a rule-less fallthrough plus an
  exclusion rule, or as a negated `InSegment`.
- The behaviour is uniform for missing attributes, `null` values and wrong-typed values, which keeps the engine
  predictable and easy to test.
