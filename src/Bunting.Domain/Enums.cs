using System.Text.Json.Serialization;

namespace Bunting.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlagType
{
    Boolean,
    String,
    Integer,
    Double,
    Object,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConditionOperator
{
    Equals,
    In,
    Contains,
    StartsWith,
    EndsWith,
    Matches,
    GreaterThan,
    LessThan,
    SemVerGreaterThan,
    SemVerLessThan,
    SemVerEquals,
    Before,
    After,
    InSegment,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiKeyType
{
    Server,
    Client,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectRole
{
    Viewer,
    Editor,
    Owner,
}
