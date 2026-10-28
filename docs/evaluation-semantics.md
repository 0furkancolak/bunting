# Evaluation semantics

This is the normative specification of `Bunting.Evaluation`. The server, the SDK and the admin playground all use the
same engine, so this document describes the behaviour of every Bunting evaluation. Reason and error code names match
the [OpenFeature specification](https://openfeature.dev/specification/types#resolution-details) exactly.

Evaluation is a **pure function**: the same ruleset and the same context always produce the same result. It performs no
I/O, parses no JSON and takes no locks (a ruleset is compiled once, see [ADR 0006](adr/0006-compiled-rulesets.md)).

## Inputs and outputs

- **Context:** an optional *targeting key* plus attributes (string, bool, numbers, dates). An empty targeting key is
  treated as absent. Attribute names are case-sensitive. `targetingKey` can also be referenced as an attribute.
- **Result:** `value`, `variant`, `reason`, `errorCode`, and `ruleId` (set when a rule produced the result).
  When `errorCode` is set the caller uses its own default value, except for `TARGETING_KEY_MISSING` (see below).

## Resolution order

The steps are evaluated in this order. The first step that produces an outcome wins.

| # | Condition | Reason | Error code | Variant served |
|---|-----------|--------|-----------|----------------|
| 1 | Flag not in the ruleset | `ERROR` | `FLAG_NOT_FOUND` | none |
| – | Flag definition is invalid (see [Compilation](#compilation)) | `ERROR` | `PARSE_ERROR` | none |
| 5 | Requested type ≠ flag type | `ERROR` | `TYPE_MISMATCH` | none |
| 2 | Flag archived, or disabled in the environment (**kill switch**) | `DISABLED` | – | `OffVariant` |
| 3 | A rule matches (rules are tried in order; first match wins) | `TARGETING_MATCH`, or `SPLIT` if the rule serves a rollout | – | the rule's variant; `ruleId` is set |
| 4 | No rule matches | `DEFAULT`, or `SPLIT` if the fallthrough is a rollout | – | the fallthrough variant |

Step 5 (type check) applies to *every* outcome that would carry a value, so it is checked right after the lookup;
the observable behaviour is that a mismatching type never yields a value, not even for disabled flags. The only
precedence rule is that `FLAG_NOT_FOUND` / `PARSE_ERROR` win over `TYPE_MISMATCH`. Callers that do not request a type
(e.g. OFREP bulk evaluation) skip the check.

A rule matches when **all** its conditions match (AND). A rule without conditions matches everyone.

## Conditions

A condition is `attribute operator values [negate]`.

### Evaluable vs. not evaluable

A condition is *evaluable* when the attribute is present (non-null) **and** its value has a type the operator can use.
A condition that is not evaluable **never matches, even if `Negate` is set** ([ADR 0004](adr/0004-missing-attributes-never-match.md)).
`Negate` only inverts the result of evaluable conditions. The sole exception is `InSegment`, which is always evaluable.

### Operators

| Operator | Attribute type | Semantics |
|----------|----------------|-----------|
| `Equals`, `In` | string / bool / number | Matches if the attribute equals **any** value. Strings: ordinal, case-sensitive. Booleans compare to the literals `true` / `false`. Numbers compare numerically (`5` equals `"5.0"`). (`Equals` conventionally carries one value; the engine treats both identically.) |
| `Contains`, `StartsWith`, `EndsWith` | string | Ordinal, case-sensitive, matches if **any** value matches. |
| `Matches` | string | .NET regex (culture-invariant), **50 ms timeout**, compiled once and cached. A timeout counts as not evaluable. Uses the first value. |
| `GreaterThan`, `LessThan` | number or numeric string | Strict comparison against the first value; `NaN`/infinity are not evaluable. |
| `SemVerGreaterThan`, `SemVerLessThan`, `SemVerEquals` | string | [SemVer 2.0](https://semver.org) precedence (pre-release < release, build metadata ignored). One to three numeric components are accepted (`1.2` = `1.2.0`); a leading `v` is rejected. Unparsable attributes are not evaluable. |
| `Before`, `After` | `DateTimeOffset`, `DateTime`, or ISO 8601 string | Instant comparison; strings without an offset are UTC. Strict (`Before` is `<`, `After` is `>`). |
| `InSegment` | – (attribute ignored) | Matches if the context is a member of **any** listed segment. |

A condition whose operand cannot be parsed at compile time (invalid regex, non-numeric `GreaterThan` operand, …) or that
has no values never matches (also not negated) and is reported as a compile warning.

### Normalisation

- All string comparisons are ordinal and case-sensitive, **except** the `country` attribute: it compares
  case-insensitively (equivalent to normalising both sides to upper case), so `"tr"` equals `"TR"`. `Matches` on
  `country` upper-cases the input first.
- Attribute names are never normalised.

### Segments

A context is a member of a segment when, in this order:

1. the targeting key is in `Excluded` → **not a member**;
2. the targeting key is in `Included` → **member**;
3. the segment has conditions and **all** of them match → **member**;
4. otherwise → not a member.

Segments may reference other segments through `InSegment`. Nesting is limited to depth 8; deeper references (and
cycles) never match and cycles are reported as compile warnings. Unknown segment keys never match.
`Negate` on `InSegment` means "not in segment" and works even without a targeting key.

## Rollouts

A rollout is an ordered list of `(variant, weight)`; weights are non-negative integers that **sum to 10 000**
(0.01 % precision).

```
bucket = MurmurHash3_x86_32(UTF8("{flagKey}.{salt}.{bucketValue}"), seed 0) % 10 000
```

- `salt` is the rollout's salt, empty by default (so the hashed string is `flag..value`).
- `bucketValue` is the targeting key, or the attribute named by `BucketBy`. Non-string values are converted using the
  invariant culture (`true`/`false` for booleans); `country` is upper-cased.
- The variant is the first entry whose cumulative weight exceeds the bucket.

**Stickiness.** The same value always lands in the same bucket, and a variant owns a fixed range of buckets
determined by the weights before it. If you grow a rollout by increasing the weight of the *first* listed variant and
shrinking the later ones (`on: 1% → 10% → 100%`, listing `on` first), no user that already received `on` ever flips
back. Different flags, and different salts, hash independently.

**Missing bucketing value.**

- In a **rule** with a rollout: the rule does not match and evaluation continues with the next rule.
- In the **fallthrough** with a rollout: the result is the `OffVariant` with reason `ERROR` and error code
  `TARGETING_KEY_MISSING`. The value is carried so that callers may still use it.

## Compilation

`CompiledRuleset.Compile` never throws on malformed content; it reports `Diagnostics`.

- **Errors** make the flag evaluate to `PARSE_ERROR` (other flags are unaffected): variant values that do not match
  the flag type (`Integer` requires an integral JSON number, `Object` an object or array), unknown variant references,
  a serve that is not exactly one of variant/rollout, rollout weights that are negative or do not sum to 10 000, a flag
  without variants, duplicate flag keys (the first definition wins).
- **Warnings** neutralise only the affected condition: invalid regex/number/version/date operands, empty values,
  unknown segments, segment cycles.
