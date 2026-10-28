using System.Text.Json;

namespace Bunting.Domain;

public sealed class Project
{
    public Guid Id { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }
}

public sealed class ProjectEnvironment
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }

    /// <summary>Monotonic ruleset version; bumped on every change affecting this environment.</summary>
    public long RulesetVersion { get; set; }

    /// <summary>Display order within the project (creation order).</summary>
    public int Position { get; set; }
}

public sealed class Flag
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public FlagType Type { get; set; }
    public Dictionary<string, JsonElement> Variants { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public bool Archived { get; set; }
    public bool ClientSideAvailable { get; set; }

    /// <summary>Optimistic concurrency token (ETag).</summary>
    public long Version { get; set; }
}

public sealed class FlagEnvironmentConfig
{
    public Guid FlagId { get; set; }
    public Guid EnvironmentId { get; set; }
    public bool Enabled { get; set; }
    public required string OffVariant { get; set; }
    public List<Rule> Rules { get; set; } = [];
    public required Serve Fallthrough { get; set; }

    /// <summary>Optimistic concurrency token.</summary>
    public long Version { get; set; }
}

public sealed class Segment
{
    public Guid Id { get; set; }
    public Guid EnvironmentId { get; set; }
    public required string Key { get; set; }
    public List<string> Included { get; set; } = [];
    public List<string> Excluded { get; set; } = [];
    public List<Condition> Conditions { get; set; } = [];

    /// <summary>Optimistic concurrency token (ETag).</summary>
    public long Version { get; set; }
}

public sealed class ApiKey
{
    public Guid Id { get; set; }
    public Guid EnvironmentId { get; set; }
    public ApiKeyType Type { get; set; }
    public required string Name { get; set; }

    /// <summary>Only the SHA-256 hash is stored; the plaintext key is shown once at creation.</summary>
    public required byte[] KeyHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public const string ServerPrefix = "bnt_srv_";
    public const string ClientPrefix = "bnt_cli_";
}

public sealed class AuditEntry
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? EnvironmentId { get; set; }
    public required string Actor { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public required string Action { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
}
