# ADR 0005: Deterministic rollout bucketing with MurmurHash3

- Status: Accepted

## Context
Percentage rollouts must give the same user the same answer on every call, in every process, in every SDK language
that evaluates locally, and must not reshuffle users when the percentage grows.

## Decision
`bucket = MurmurHash3_x86_32(UTF8("{flagKey}.{salt}.{bucketValue}")) % 10 000`, with weights in basis points.
Variants own consecutive bucket ranges in listed order. Murmur3 is implemented in-repo (about 40 lines) and verified
against published reference vectors, which avoids a dependency and keeps `Bunting.Evaluation` infrastructure-free.
Including the flag key and salt decorrelates rollouts of different flags.

## Consequences
- Stability across processes, restarts and versions as long as the formula is unchanged (changing it is a breaking
  change and needs a new ADR).
- Growing the first variant's weight is sticky; reordering variants or growing a later variant moves users.
- Hash input is built into a stack buffer, so bucketing allocates nothing for keys up to ~250 bytes.
