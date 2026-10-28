using System.Text.Json;

namespace Bunting.Evaluation;

/// <summary>Resolution reasons; the wire strings match the OpenFeature specification exactly.</summary>
public enum EvaluationReason
{
    Static,
    Default,
    TargetingMatch,
    Split,
    Cached,
    Disabled,
    Unknown,
    Stale,
    Error,
}

/// <summary>Error codes; the wire strings match the OpenFeature specification exactly.</summary>
public enum EvaluationErrorCode
{
    None,
    ProviderNotReady,
    FlagNotFound,
    ParseError,
    TypeMismatch,
    TargetingKeyMissing,
    InvalidContext,
    General,
}

public static class EvaluationWireNames
{
    public static string ToWireString(this EvaluationReason reason) => reason switch
    {
        EvaluationReason.Static => "STATIC",
        EvaluationReason.Default => "DEFAULT",
        EvaluationReason.TargetingMatch => "TARGETING_MATCH",
        EvaluationReason.Split => "SPLIT",
        EvaluationReason.Cached => "CACHED",
        EvaluationReason.Disabled => "DISABLED",
        EvaluationReason.Unknown => "UNKNOWN",
        EvaluationReason.Stale => "STALE",
        EvaluationReason.Error => "ERROR",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>Returns <see langword="null"/> for <see cref="EvaluationErrorCode.None"/>.</summary>
    public static string? ToWireString(this EvaluationErrorCode code) => code switch
    {
        EvaluationErrorCode.None => null,
        EvaluationErrorCode.ProviderNotReady => "PROVIDER_NOT_READY",
        EvaluationErrorCode.FlagNotFound => "FLAG_NOT_FOUND",
        EvaluationErrorCode.ParseError => "PARSE_ERROR",
        EvaluationErrorCode.TypeMismatch => "TYPE_MISMATCH",
        EvaluationErrorCode.TargetingKeyMissing => "TARGETING_KEY_MISSING",
        EvaluationErrorCode.InvalidContext => "INVALID_CONTEXT",
        EvaluationErrorCode.General => "GENERAL",
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };
}

/// <summary>
/// Outcome of one evaluation. When <see cref="ErrorCode"/> is not <see cref="EvaluationErrorCode.None"/> the caller
/// should use its own default, except for <see cref="EvaluationErrorCode.TargetingKeyMissing"/> where
/// <see cref="Value"/> carries the off variant.
/// </summary>
public readonly struct EvaluationResult
{
    internal EvaluationResult(
        string flagKey,
        JsonElement value,
        string? variant,
        EvaluationReason reason,
        EvaluationErrorCode errorCode = EvaluationErrorCode.None,
        string? ruleId = null)
    {
        FlagKey = flagKey;
        Value = value;
        Variant = variant;
        Reason = reason;
        ErrorCode = errorCode;
        RuleId = ruleId;
    }

    public string FlagKey { get; }

    /// <summary><c>default</c> (ValueKind Undefined) when no value was resolved.</summary>
    public JsonElement Value { get; }

    public string? Variant { get; }

    public EvaluationReason Reason { get; }

    public EvaluationErrorCode ErrorCode { get; }

    /// <summary>Id of the matching rule, set when <see cref="Reason"/> stems from a rule.</summary>
    public string? RuleId { get; }

    public bool IsError => ErrorCode != EvaluationErrorCode.None;

    internal static EvaluationResult Error(string flagKey, EvaluationErrorCode code) =>
        new(flagKey, default, null, EvaluationReason.Error, code);
}
