using Bunting.Domain;

namespace Bunting.Evaluation;

internal static class RulesetCompiler
{
    public static CompiledRuleset Compile(RulesetSnapshot snapshot)
    {
        var diagnostics = new List<RulesetDiagnostic>();
        var segments = CompileSegments(snapshot, diagnostics);

        var flags = new Dictionary<string, CompiledFlag>(StringComparer.Ordinal);
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in snapshot.Flags)
        {
            var compiled = CompileFlag(flag, segments, diagnostics);
            if (compiled is null)
            {
                invalid.Add(flag.Key);
            }
            else if (!flags.TryAdd(flag.Key, compiled))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, flag.Key, "Duplicate flag key; the first definition is used."));
            }
        }

        var infos = snapshot.Flags
            .GroupBy(f => f.Key, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new FlagInfo(f.Key, f.Type, f.Archived, f.ClientSideAvailable, !invalid.Contains(f.Key)))
            .ToArray();
        return new CompiledRuleset(snapshot.Version, flags, invalid, diagnostics, infos);
    }

    private static Dictionary<string, CompiledSegment> CompileSegments(RulesetSnapshot snapshot, List<RulesetDiagnostic> diagnostics)
    {
        var segments = new Dictionary<string, CompiledSegment>(StringComparer.Ordinal);
        foreach (var source in snapshot.Segments)
        {
            segments.TryAdd(source.Key, new CompiledSegment());
        }

        foreach (var source in snapshot.Segments)
        {
            var conditions = source.Conditions
                .Select(c => new CompiledCondition(
                    c, segments, message => diagnostics.Add(new(DiagnosticSeverity.Warning, null, $"Segment '{source.Key}': {message}"))))
                .ToArray();
            segments[source.Key].Initialize(source.Included ?? [], source.Excluded ?? [], conditions);
        }

        DetectSegmentCycles(snapshot, diagnostics);
        return segments;
    }

    private static void DetectSegmentCycles(RulesetSnapshot snapshot, List<RulesetDiagnostic> diagnostics)
    {
        var edges = snapshot.Segments
            .GroupBy(s => s.Key)
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(s => s.Conditions)
                    .Where(c => c.Operator == ConditionOperator.InSegment)
                    .SelectMany(c => c.Values ?? [])
                    .ToArray());

        var state = new Dictionary<string, int>(); // 1 = visiting, 2 = done
        foreach (var key in edges.Keys)
        {
            Visit(key);
        }

        void Visit(string key)
        {
            if (state.TryGetValue(key, out var s))
            {
                if (s == 1)
                {
                    diagnostics.Add(new(DiagnosticSeverity.Warning, null, $"Segment '{key}' is part of a reference cycle; nested membership beyond the depth limit never matches."));
                }

                return;
            }

            state[key] = 1;
            foreach (var next in edges[key])
            {
                if (edges.ContainsKey(next))
                {
                    Visit(next);
                }
            }

            state[key] = 2;
        }
    }

    private static CompiledFlag? CompileFlag(
        FlagSnapshot flag, Dictionary<string, CompiledSegment> segments, List<RulesetDiagnostic> diagnostics)
    {
        var errors = new List<string>();
        void Error(string message) => errors.Add(message);
        void Warn(string message) => diagnostics.Add(new(DiagnosticSeverity.Warning, flag.Key, message));

        var variants = new List<CompiledVariant>();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, value) in flag.Variants)
        {
            if (!CompiledFlag.ValueMatchesType(flag.Type, value))
            {
                Error($"Variant '{name}' value is not a valid {flag.Type}.");
            }

            indexByName[name] = variants.Count;
            variants.Add(new CompiledVariant(name, value));
        }

        int IndexOf(string? name)
        {
            if (name is not null && indexByName.TryGetValue(name, out var index))
            {
                return index;
            }

            Error($"Unknown variant '{name}'.");
            return 0;
        }

        CompiledServe CompileServe(Serve? serve, string where)
        {
            if (serve is null || (serve.Variant is null) == (serve.Rollout is null))
            {
                Error($"{where}: serve must specify exactly one of variant or rollout.");
                return new CompiledServe(0, null);
            }

            if (serve.Rollout is null)
            {
                return new CompiledServe(IndexOf(serve.Variant), null);
            }

            var rollout = serve.Rollout;
            if (rollout.Variants is not { Count: > 0 } || rollout.Variants.Any(v => v.Weight < 0)
                || rollout.Variants.Sum(v => (long)v.Weight) != Rollout.TotalWeight)
            {
                Error($"{where}: rollout weights must be non-negative and sum to {Rollout.TotalWeight}.");
                return new CompiledServe(0, null);
            }

            return new CompiledServe(0, new CompiledRollout(flag.Key, rollout, IndexOf));
        }

        if (variants.Count == 0)
        {
            Error("Flag has no variants.");
        }

        var offVariant = IndexOf(flag.OffVariant);
        var rules = new List<CompiledRule>();
        foreach (var rule in flag.Rules ?? [])
        {
            var conditions = (rule.Conditions ?? [])
                .Select(c => new CompiledCondition(c, segments, m => Warn($"Rule '{rule.Id}': {m}")))
                .ToArray();
            rules.Add(new CompiledRule(rule.Id, conditions, CompileServe(rule.Serve, $"Rule '{rule.Id}'")));
        }

        var fallthrough = CompileServe(flag.Fallthrough, "Fallthrough");

        if (errors.Count > 0)
        {
            foreach (var message in errors)
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, flag.Key, message));
            }

            return null;
        }

        return new CompiledFlag(flag.Key, flag.Type, [.. variants], flag.Enabled && !flag.Archived, offVariant, [.. rules], fallthrough);
    }
}
