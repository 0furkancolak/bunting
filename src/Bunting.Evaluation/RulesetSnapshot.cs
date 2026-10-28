using System.Text.Json;
using Bunting.Domain;

namespace Bunting.Evaluation;

/// <summary>
/// Serializable evaluation input: everything needed to evaluate the flags of one environment.
/// Served by the server (<c>GET /api/v1/sdk/ruleset</c>) and compiled by <see cref="CompiledRuleset"/>.
/// </summary>
public sealed record RulesetSnapshot(
    long Version,
    IReadOnlyList<FlagSnapshot> Flags,
    IReadOnlyList<SegmentSnapshot> Segments);

public sealed record FlagSnapshot(
    string Key,
    FlagType Type,
    IReadOnlyDictionary<string, JsonElement> Variants,
    bool Enabled,
    bool Archived,
    string OffVariant,
    IReadOnlyList<Rule> Rules,
    Serve Fallthrough,
    bool ClientSideAvailable = false);

public sealed record SegmentSnapshot(
    string Key,
    IReadOnlyList<string> Included,
    IReadOnlyList<string> Excluded,
    IReadOnlyList<Condition> Conditions);

public enum DiagnosticSeverity
{
    /// <summary>The flag is unusable and evaluates to <c>PARSE_ERROR</c>.</summary>
    Error,

    /// <summary>A part of the definition is ignored (e.g. a condition that can never match).</summary>
    Warning,
}

public sealed record RulesetDiagnostic(DiagnosticSeverity Severity, string? FlagKey, string Message);
