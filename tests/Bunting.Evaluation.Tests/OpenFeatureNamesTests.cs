namespace Bunting.Evaluation.Tests;

public class OpenFeatureNamesTests
{
    [Theory]
    [InlineData(EvaluationReason.Static, "STATIC")]
    [InlineData(EvaluationReason.Default, "DEFAULT")]
    [InlineData(EvaluationReason.TargetingMatch, "TARGETING_MATCH")]
    [InlineData(EvaluationReason.Split, "SPLIT")]
    [InlineData(EvaluationReason.Cached, "CACHED")]
    [InlineData(EvaluationReason.Disabled, "DISABLED")]
    [InlineData(EvaluationReason.Unknown, "UNKNOWN")]
    [InlineData(EvaluationReason.Stale, "STALE")]
    [InlineData(EvaluationReason.Error, "ERROR")]
    public void Reasons_match_the_specification(EvaluationReason reason, string wire) =>
        Assert.Equal(wire, reason.ToWireString());

    [Theory]
    [InlineData(EvaluationErrorCode.ProviderNotReady, "PROVIDER_NOT_READY")]
    [InlineData(EvaluationErrorCode.FlagNotFound, "FLAG_NOT_FOUND")]
    [InlineData(EvaluationErrorCode.ParseError, "PARSE_ERROR")]
    [InlineData(EvaluationErrorCode.TypeMismatch, "TYPE_MISMATCH")]
    [InlineData(EvaluationErrorCode.TargetingKeyMissing, "TARGETING_KEY_MISSING")]
    [InlineData(EvaluationErrorCode.InvalidContext, "INVALID_CONTEXT")]
    [InlineData(EvaluationErrorCode.General, "GENERAL")]
    public void Error_codes_match_the_specification(EvaluationErrorCode code, string wire) =>
        Assert.Equal(wire, code.ToWireString());

    [Fact]
    public void No_error_has_no_wire_string() => Assert.Null(EvaluationErrorCode.None.ToWireString());

    [Fact]
    public void Every_enum_member_has_a_wire_string()
    {
        foreach (var r in Enum.GetValues<EvaluationReason>())
        {
            Assert.NotEmpty(r.ToWireString());
        }

        foreach (var c in Enum.GetValues<EvaluationErrorCode>().Where(c => c != EvaluationErrorCode.None))
        {
            Assert.NotEmpty(c.ToWireString()!);
        }
    }
}
