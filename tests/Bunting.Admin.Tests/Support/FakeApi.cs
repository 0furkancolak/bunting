using System.Text.Json;
using Bunting.Admin.Api;
using Bunting.Domain;

namespace Bunting.Admin.Tests.Support;

/// <summary>An in-memory Management API that records every write, so tests can prove what a role could (not) do.</summary>
public sealed class FakeApi : IBuntingApi
{
    public const string Project = "demo";

    public ProjectRole? Role { get; set; } = ProjectRole.Owner;

    public bool InstanceAdmin { get; set; }

    public List<string> Writes { get; } = [];

    public List<FlagInfo> Flags { get; } =
    [
        NewFlag("new-checkout", FlagType.Boolean, ["checkout", "web"]),
        NewFlag("banner-text", FlagType.String, ["marketing"]),
    ];

    public Dictionary<string, FlagConfigInfo> Configs { get; } = [];

    public List<SegmentInfo> Segments { get; } = [new(Guid.NewGuid(), "beta-users", ["alice"], [], [new Condition("plan", ConditionOperator.Equals, ["pro"])], 1)];

    public List<AuditItem> Audit { get; } = [];

    public List<ApiKeyInfo> Keys { get; } = [new(Guid.NewGuid(), "backend", ApiKeyType.Server, "a1b2", DateTimeOffset.UtcNow, null)];

    public List<MemberInfo> Members { get; } = [new("u1", "alice@example.com", ProjectRole.Editor)];

    public List<UserInfo> Users { get; } = [new("u1", "alice@example.com", false)];

    public PlaygroundResponse? PlaygroundAnswer { get; set; }

    public string? LastPlaygroundContext { get; private set; }

    public static FlagInfo NewFlag(string key, FlagType type, List<string> tags)
    {
        var variants = type == FlagType.Boolean
            ? new Dictionary<string, JsonElement> { ["on"] = JsonSerializer.SerializeToElement(true), ["off"] = JsonSerializer.SerializeToElement(false) }
            : new Dictionary<string, JsonElement> { ["a"] = JsonSerializer.SerializeToElement("A"), ["b"] = JsonSerializer.SerializeToElement("B") };
        return new FlagInfo(
            Guid.NewGuid(), key, key.Replace('-', ' '), null, type, variants, tags, false, false, 1,
            new Dictionary<string, FlagEnvState> { ["development"] = new(false, 1), ["staging"] = new(false, 1), ["production"] = new(true, 1) });
    }

    private static string Cfg(string environment, string flag) => $"{environment}/{flag}";

    private FlagConfigInfo ConfigOf(string environment, string flag)
    {
        var info = Flags.Single(f => f.Key == flag);
        return Configs.TryGetValue(Cfg(environment, flag), out var config)
            ? config
            : new FlagConfigInfo(flag, environment, info.Environments![environment].Enabled, info.Variants.Keys.Last(), [], Serve.Single(info.Variants.Keys.First()), 1);
    }

    private void Record(string write) => Writes.Add(write);

    public Task<MeInfo> GetMeAsync(CancellationToken ct = default) =>
        Task.FromResult(new MeInfo("tester", "tester@example.com", InstanceAdmin, InstanceAdmin ? ProjectRole.Owner : null, Role is { } r ? [new Membership(Project, r)] : []));

    public Task<List<ProjectInfo>> GetProjectsAsync(CancellationToken ct = default) => Task.FromResult(new List<ProjectInfo> { ProjectInfoValue });

    public static ProjectInfo ProjectInfoValue { get; } = new(
        Guid.NewGuid(), Project, "Demo shop",
        [new(Guid.NewGuid(), "development", "development", 1), new(Guid.NewGuid(), "staging", "staging", 1), new(Guid.NewGuid(), "production", "production", 1)]);

    public Task<ProjectInfo> GetProjectAsync(string project, CancellationToken ct = default) => Task.FromResult(ProjectInfoValue);

    public Task<ProjectInfo> CreateProjectAsync(string key, string name, CancellationToken ct = default)
    {
        Record($"create project {key}");
        return Task.FromResult(ProjectInfoValue with { Key = key, Name = name });
    }

    public Task<List<FlagInfo>> GetFlagsAsync(string project, string? search = null, string? tag = null, bool includeArchived = false, CancellationToken ct = default) =>
        Task.FromResult(Flags
            .Where(f => string.IsNullOrWhiteSpace(search) || f.Key.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(f => tag is null || f.Tags.Contains(tag))
            .Where(f => includeArchived || !f.Archived)
            .ToList());

    public Task<FlagInfo> GetFlagAsync(string project, string flag, CancellationToken ct = default) => Task.FromResult(Flags.Single(f => f.Key == flag));

    public Task<FlagInfo> CreateFlagAsync(string project, CreateFlagRequest request, CancellationToken ct = default)
    {
        Record($"create flag {request.Key}");
        var flag = NewFlag(request.Key, request.Type, request.Tags);
        Flags.Add(flag);
        return Task.FromResult(flag);
    }

    public Task<FlagInfo> UpdateFlagAsync(string project, string flag, UpdateFlagRequest request, long version, CancellationToken ct = default)
    {
        Record($"update flag {flag}");
        return Task.FromResult(Flags.Single(f => f.Key == flag));
    }

    public Task<FlagConfigInfo> GetConfigAsync(string project, string environment, string flag, CancellationToken ct = default) => Task.FromResult(ConfigOf(environment, flag));

    public Task<FlagConfigInfo> PutConfigAsync(string project, string environment, string flag, FlagConfigRequest request, long version, CancellationToken ct = default)
    {
        Record($"put config {environment}/{flag}");
        var config = new FlagConfigInfo(flag, environment, request.Enabled, request.OffVariant, request.Rules, request.Fallthrough, version + 1);
        Configs[Cfg(environment, flag)] = config;
        return Task.FromResult(config);
    }

    public Task<FlagConfigInfo> SetEnabledAsync(string project, string environment, string flag, bool enabled, long version, CancellationToken ct = default)
    {
        Record($"set enabled {environment}/{flag}={enabled}");
        var config = ConfigOf(environment, flag) with { Enabled = enabled, Version = version + 1 };
        Configs[Cfg(environment, flag)] = config;
        return Task.FromResult(config);
    }

    public Task<List<SegmentInfo>> GetSegmentsAsync(string project, string environment, CancellationToken ct = default) => Task.FromResult(Segments.ToList());

    public Task<SegmentInfo> CreateSegmentAsync(string project, string environment, SegmentRequest request, CancellationToken ct = default)
    {
        Record($"create segment {request.Key}");
        var segment = new SegmentInfo(Guid.NewGuid(), request.Key, request.Included, request.Excluded, request.Conditions, 1);
        Segments.Add(segment);
        return Task.FromResult(segment);
    }

    public Task<SegmentInfo> UpdateSegmentAsync(string project, string environment, string segment, SegmentRequest request, long version, CancellationToken ct = default)
    {
        Record($"update segment {segment}");
        return Task.FromResult(Segments.Single(s => s.Key == segment));
    }

    public Task DeleteSegmentAsync(string project, string environment, string segment, long version, CancellationToken ct = default)
    {
        Record($"delete segment {segment}");
        Segments.RemoveAll(s => s.Key == segment);
        return Task.CompletedTask;
    }

    public Task<List<ApiKeyInfo>> GetApiKeysAsync(string project, string environment, CancellationToken ct = default) => Task.FromResult(Keys.ToList());

    public Task<CreatedApiKey> CreateApiKeyAsync(string project, string environment, string name, ApiKeyType type, CancellationToken ct = default)
    {
        Record($"create key {name}");
        return Task.FromResult(new CreatedApiKey(Guid.NewGuid(), name, type, "bnt_srv_plaintext_once", "once", DateTimeOffset.UtcNow));
    }

    public Task RevokeApiKeyAsync(string project, string environment, Guid id, CancellationToken ct = default)
    {
        Record($"revoke key {id}");
        return Task.CompletedTask;
    }

    public Task<AuditPage> QueryAuditAsync(string project, AuditQuery query, CancellationToken ct = default) => Task.FromResult(new AuditPage(Audit.ToList(), null));

    public Task<PlaygroundResponse> EvaluateAsync(string project, string environment, string? flagKey, string contextJson, CancellationToken ct = default)
    {
        LastPlaygroundContext = contextJson;
        return Task.FromResult(PlaygroundAnswer ?? new PlaygroundResponse(1, []));
    }

    public Task<List<MemberInfo>> GetMembersAsync(string project, CancellationToken ct = default) => Task.FromResult(Members.ToList());

    public Task AddMemberAsync(string project, string email, ProjectRole role, CancellationToken ct = default)
    {
        Record($"add member {email}");
        return Task.CompletedTask;
    }

    public Task UpdateMemberAsync(string project, string userId, ProjectRole role, CancellationToken ct = default)
    {
        Record($"update member {userId}");
        return Task.CompletedTask;
    }

    public Task RemoveMemberAsync(string project, string userId, CancellationToken ct = default)
    {
        Record($"remove member {userId}");
        return Task.CompletedTask;
    }

    public Task<List<UserInfo>> GetUsersAsync(CancellationToken ct = default) => Task.FromResult(Users.ToList());

    public Task CreateUserAsync(string email, string password, bool isInstanceAdmin, CancellationToken ct = default)
    {
        Record($"create user {email}");
        return Task.CompletedTask;
    }

    public Task DeleteUserAsync(string userId, CancellationToken ct = default)
    {
        Record($"delete user {userId}");
        return Task.CompletedTask;
    }
}
