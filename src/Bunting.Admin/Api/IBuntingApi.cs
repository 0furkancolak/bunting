namespace Bunting.Admin.Api;

/// <summary>The Management API as used by the admin panel. Writes that need <c>If-Match</c> take the version that was read.</summary>
public interface IBuntingApi
{
    Task<MeInfo> GetMeAsync(CancellationToken ct = default);

    Task<List<ProjectInfo>> GetProjectsAsync(CancellationToken ct = default);

    Task<ProjectInfo> GetProjectAsync(string project, CancellationToken ct = default);

    Task<ProjectInfo> CreateProjectAsync(string key, string name, CancellationToken ct = default);

    Task<List<FlagInfo>> GetFlagsAsync(string project, string? search = null, string? tag = null, bool includeArchived = false, CancellationToken ct = default);

    Task<FlagInfo> GetFlagAsync(string project, string flag, CancellationToken ct = default);

    Task<FlagInfo> CreateFlagAsync(string project, CreateFlagRequest request, CancellationToken ct = default);

    Task<FlagInfo> UpdateFlagAsync(string project, string flag, UpdateFlagRequest request, long version, CancellationToken ct = default);

    Task<FlagConfigInfo> GetConfigAsync(string project, string environment, string flag, CancellationToken ct = default);

    Task<FlagConfigInfo> PutConfigAsync(string project, string environment, string flag, FlagConfigRequest request, long version, CancellationToken ct = default);

    Task<FlagConfigInfo> SetEnabledAsync(string project, string environment, string flag, bool enabled, long version, CancellationToken ct = default);

    Task<List<SegmentInfo>> GetSegmentsAsync(string project, string environment, CancellationToken ct = default);

    Task<SegmentInfo> CreateSegmentAsync(string project, string environment, SegmentRequest request, CancellationToken ct = default);

    Task<SegmentInfo> UpdateSegmentAsync(string project, string environment, string segment, SegmentRequest request, long version, CancellationToken ct = default);

    Task DeleteSegmentAsync(string project, string environment, string segment, long version, CancellationToken ct = default);

    Task<List<ApiKeyInfo>> GetApiKeysAsync(string project, string environment, CancellationToken ct = default);

    Task<CreatedApiKey> CreateApiKeyAsync(string project, string environment, string name, Bunting.Domain.ApiKeyType type, CancellationToken ct = default);

    Task RevokeApiKeyAsync(string project, string environment, Guid id, CancellationToken ct = default);

    Task<AuditPage> QueryAuditAsync(string project, AuditQuery query, CancellationToken ct = default);

    Task<PlaygroundResponse> EvaluateAsync(string project, string environment, string? flagKey, string contextJson, CancellationToken ct = default);

    Task<List<MemberInfo>> GetMembersAsync(string project, CancellationToken ct = default);

    Task AddMemberAsync(string project, string email, Bunting.Domain.ProjectRole role, CancellationToken ct = default);

    Task UpdateMemberAsync(string project, string userId, Bunting.Domain.ProjectRole role, CancellationToken ct = default);

    Task RemoveMemberAsync(string project, string userId, CancellationToken ct = default);

    Task<List<UserInfo>> GetUsersAsync(CancellationToken ct = default);

    Task CreateUserAsync(string email, string password, bool isInstanceAdmin, CancellationToken ct = default);

    Task DeleteUserAsync(string userId, CancellationToken ct = default);
}
