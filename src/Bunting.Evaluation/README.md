# Bunting.Evaluation

The rule engine behind [Bunting](https://github.com/0furkancolak/bunting): a flag configuration is compiled once into an immutable
structure and evaluated without allocations (under a microsecond for a 20-rule flag). Reasons and error codes follow the
OpenFeature specification. Most applications use [`Bunting.Sdk`](https://www.nuget.org/packages/Bunting.Sdk) instead.
