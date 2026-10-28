using System.Text.Json;
using Bunting.Domain;

namespace Bunting.Admin.Api;

public sealed record EnvironmentInfo(Guid Id, string Key, string Name, long RulesetVersion);

public sealed record ProjectInfo(Guid Id, string Key, string Name, List<EnvironmentInfo> Environments);

public sealed record FlagEnvState(bool Enabled, long Version);

public sealed record FlagInfo(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    FlagType Type,
    Dictionary<string, JsonElement> Variants,
    List<string> Tags,
    bool Archived,
    bool ClientSideAvailable,
    long Version,
    Dictionary<string, FlagEnvState>? Environments);

public sealed record CreateFlagRequest(
    string Key,
    string Name,
    string? Description,
    FlagType Type,
    Dictionary<string, JsonElement> Variants,
    string? DefaultVariant,
    List<string> Tags,
    bool ClientSideAvailable);

public sealed record UpdateFlagRequest(
    string Name,
    string? Description,
    Dictionary<string, JsonElement> Variants,
    List<string> Tags,
    bool ClientSideAvailable,
    bool Archived);

public sealed record FlagConfigInfo(
    string FlagKey,
    string Environment,
    bool Enabled,
    string OffVariant,
    List<Rule> Rules,
    Serve Fallthrough,
    long Version);

public sealed record FlagConfigRequest(bool Enabled, string OffVariant, List<Rule> Rules, Serve Fallthrough);

public sealed record SegmentInfo(Guid Id, string Key, List<string> Included, List<string> Excluded, List<Condition> Conditions, long Version);

public sealed record SegmentRequest(string Key, List<string> Included, List<string> Excluded, List<Condition> Conditions);

public sealed record ApiKeyInfo(Guid Id, string Name, ApiKeyType Type, string Hint, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

public sealed record CreatedApiKey(Guid Id, string Name, ApiKeyType Type, string Key, string Hint, DateTimeOffset CreatedAt);

public sealed record AuditItem(
    Guid Id,
    DateTimeOffset Timestamp,
    string Actor,
    string EntityType,
    string EntityId,
    string Action,
    string? Environment,
    JsonElement? Before,
    JsonElement? After);

public sealed record AuditPage(List<AuditItem> Items, DateTimeOffset? NextBefore);

public sealed record AuditQuery(string? EntityType = null, string? EntityId = null, string? Actor = null, string? Environment = null, DateTimeOffset? Before = null, int Limit = 50);

public sealed record Membership(string ProjectKey, ProjectRole Role);

public sealed record MeInfo(string Name, string? Email, bool IsInstanceAdmin, ProjectRole? GlobalRole, List<Membership> Memberships)
{
    /// <summary>The caller's role in a project: instance administrators and static tokens hold one role everywhere.</summary>
    public ProjectRole? RoleIn(string projectKey) =>
        GlobalRole ?? Memberships.Where(m => m.ProjectKey == projectKey).Select(m => (ProjectRole?)m.Role).FirstOrDefault();
}

public sealed record MemberInfo(string UserId, string Email, ProjectRole Role);

public sealed record UserInfo(string Id, string Email, bool IsInstanceAdmin);

public sealed record PlaygroundResult(string FlagKey, JsonElement? Value, string? Variant, string Reason, string? ErrorCode, string? RuleId);

public sealed record PlaygroundResponse(long RulesetVersion, List<PlaygroundResult> Results);

public sealed record LoginResult(string AccessToken);
