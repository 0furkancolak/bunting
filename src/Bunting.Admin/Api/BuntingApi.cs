using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bunting.Domain;

namespace Bunting.Admin.Api;

/// <summary>Provides the Management API bearer token of the signed-in user.</summary>
public interface IAccessTokenProvider
{
    ValueTask<string?> GetTokenAsync();
}

/// <summary>HTTP implementation of <see cref="IBuntingApi"/>; the admin panel talks to the server only through this class.</summary>
public sealed class BuntingApi(HttpClient http, IAccessTokenProvider tokens) : IBuntingApi
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Signs in against the server and returns the bearer token. Used by the login endpoint, not by pages.</summary>
    public static async Task<string> LoginAsync(HttpClient http, string email, string password, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/auth/login", new { email, password }, Json, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await ToExceptionAsync(response, ct);
        }

        return (await response.Content.ReadFromJsonAsync<LoginResult>(Json, ct))!.AccessToken;
    }

    private static string P(string project) => $"api/v1/projects/{Uri.EscapeDataString(project)}";

    private static string E(string project, string environment) => $"{P(project)}/environments/{Uri.EscapeDataString(environment)}";

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, long? version, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (await tokens.GetTokenAsync() is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (version is { } v)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{v}\"");
        }

        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                throw await ToExceptionAsync(response, ct);
            }
            finally
            {
                response.Dispose();
            }
        }

        return response;
    }

    private async Task<T> ReadAsync<T>(HttpMethod method, string url, object? body = null, long? version = null, CancellationToken ct = default)
    {
        using var response = await SendAsync(method, url, body, version, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task SendOnlyAsync(HttpMethod method, string url, object? body = null, long? version = null, CancellationToken ct = default)
    {
        using var response = await SendAsync(method, url, body, version, ct);
    }

    private static async Task<ApiException> ToExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string title = response.ReasonPhrase ?? response.StatusCode.ToString();
        string? detail = null;
        long? currentVersion = null;
        Dictionary<string, string[]>? errors = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            if (problem.ValueKind == JsonValueKind.Object)
            {
                title = problem.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : title;
                detail = problem.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                currentVersion = problem.TryGetProperty("currentVersion", out var v) && v.TryGetInt64(out var n) ? n : null;
                if (problem.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
                {
                    errors = e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray());
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // not a problem document
        }

        return new ApiException(response.StatusCode, title, detail, currentVersion, errors);
    }

    public Task<MeInfo> GetMeAsync(CancellationToken ct = default) => ReadAsync<MeInfo>(HttpMethod.Get, "api/v1/auth/me", ct: ct);

    public Task<List<ProjectInfo>> GetProjectsAsync(CancellationToken ct = default) => ReadAsync<List<ProjectInfo>>(HttpMethod.Get, "api/v1/projects", ct: ct);

    public Task<ProjectInfo> GetProjectAsync(string project, CancellationToken ct = default) => ReadAsync<ProjectInfo>(HttpMethod.Get, P(project), ct: ct);

    public Task<ProjectInfo> CreateProjectAsync(string key, string name, CancellationToken ct = default) =>
        ReadAsync<ProjectInfo>(HttpMethod.Post, "api/v1/projects", new { key, name }, ct: ct);

    public Task<List<FlagInfo>> GetFlagsAsync(string project, string? search = null, string? tag = null, bool includeArchived = false, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add("search=" + Uri.EscapeDataString(search));
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            query.Add("tag=" + Uri.EscapeDataString(tag));
        }

        if (includeArchived)
        {
            query.Add("archived=true");
        }

        return ReadAsync<List<FlagInfo>>(HttpMethod.Get, $"{P(project)}/flags" + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty), ct: ct);
    }

    public Task<FlagInfo> GetFlagAsync(string project, string flag, CancellationToken ct = default) =>
        ReadAsync<FlagInfo>(HttpMethod.Get, $"{P(project)}/flags/{Uri.EscapeDataString(flag)}", ct: ct);

    public Task<FlagInfo> CreateFlagAsync(string project, CreateFlagRequest request, CancellationToken ct = default) =>
        ReadAsync<FlagInfo>(HttpMethod.Post, $"{P(project)}/flags", request, ct: ct);

    public Task<FlagInfo> UpdateFlagAsync(string project, string flag, UpdateFlagRequest request, long version, CancellationToken ct = default) =>
        ReadAsync<FlagInfo>(HttpMethod.Put, $"{P(project)}/flags/{Uri.EscapeDataString(flag)}", request, version, ct);

    public Task<FlagConfigInfo> GetConfigAsync(string project, string environment, string flag, CancellationToken ct = default) =>
        ReadAsync<FlagConfigInfo>(HttpMethod.Get, $"{E(project, environment)}/flags/{Uri.EscapeDataString(flag)}", ct: ct);

    public Task<FlagConfigInfo> PutConfigAsync(string project, string environment, string flag, FlagConfigRequest request, long version, CancellationToken ct = default) =>
        ReadAsync<FlagConfigInfo>(HttpMethod.Put, $"{E(project, environment)}/flags/{Uri.EscapeDataString(flag)}", request, version, ct);

    public Task<FlagConfigInfo> SetEnabledAsync(string project, string environment, string flag, bool enabled, long version, CancellationToken ct = default) =>
        ReadAsync<FlagConfigInfo>(HttpMethod.Patch, $"{E(project, environment)}/flags/{Uri.EscapeDataString(flag)}", new { enabled }, version, ct);

    public Task<List<SegmentInfo>> GetSegmentsAsync(string project, string environment, CancellationToken ct = default) =>
        ReadAsync<List<SegmentInfo>>(HttpMethod.Get, $"{E(project, environment)}/segments", ct: ct);

    public Task<SegmentInfo> CreateSegmentAsync(string project, string environment, SegmentRequest request, CancellationToken ct = default) =>
        ReadAsync<SegmentInfo>(HttpMethod.Post, $"{E(project, environment)}/segments", request, ct: ct);

    public Task<SegmentInfo> UpdateSegmentAsync(string project, string environment, string segment, SegmentRequest request, long version, CancellationToken ct = default) =>
        ReadAsync<SegmentInfo>(HttpMethod.Put, $"{E(project, environment)}/segments/{Uri.EscapeDataString(segment)}", request, version, ct);

    public Task DeleteSegmentAsync(string project, string environment, string segment, long version, CancellationToken ct = default) =>
        SendOnlyAsync(HttpMethod.Delete, $"{E(project, environment)}/segments/{Uri.EscapeDataString(segment)}", version: version, ct: ct);

    public Task<List<ApiKeyInfo>> GetApiKeysAsync(string project, string environment, CancellationToken ct = default) =>
        ReadAsync<List<ApiKeyInfo>>(HttpMethod.Get, $"{E(project, environment)}/api-keys", ct: ct);

    public Task<CreatedApiKey> CreateApiKeyAsync(string project, string environment, string name, ApiKeyType type, CancellationToken ct = default) =>
        ReadAsync<CreatedApiKey>(HttpMethod.Post, $"{E(project, environment)}/api-keys", new { name, type }, ct: ct);

    public Task RevokeApiKeyAsync(string project, string environment, Guid id, CancellationToken ct = default) =>
        SendOnlyAsync(HttpMethod.Delete, $"{E(project, environment)}/api-keys/{id}", ct: ct);

    public Task<AuditPage> QueryAuditAsync(string project, AuditQuery query, CancellationToken ct = default)
    {
        var parts = new List<string> { $"limit={query.Limit}" };
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add($"{name}={Uri.EscapeDataString(value)}");
            }
        }

        Add("entityType", query.EntityType);
        Add("entityId", query.EntityId);
        Add("actor", query.Actor);
        Add("environment", query.Environment);
        Add("before", query.Before?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return ReadAsync<AuditPage>(HttpMethod.Get, $"{P(project)}/audit?{string.Join('&', parts)}", ct: ct);
    }

    public Task<PlaygroundResponse> EvaluateAsync(string project, string environment, string? flagKey, string contextJson, CancellationToken ct = default)
    {
        using var context = JsonDocument.Parse(string.IsNullOrWhiteSpace(contextJson) ? "{}" : contextJson);
        return ReadAsync<PlaygroundResponse>(HttpMethod.Post, $"{E(project, environment)}/evaluate", new { flagKey, context = context.RootElement.Clone() }, ct: ct);
    }

    public Task<List<MemberInfo>> GetMembersAsync(string project, CancellationToken ct = default) =>
        ReadAsync<List<MemberInfo>>(HttpMethod.Get, $"{P(project)}/members", ct: ct);

    public Task AddMemberAsync(string project, string email, ProjectRole role, CancellationToken ct = default) =>
        SendOnlyAsync(HttpMethod.Post, $"{P(project)}/members", new { email, role }, ct: ct);

    public Task UpdateMemberAsync(string project, string userId, ProjectRole role, CancellationToken ct = default) =>
        SendOnlyAsync(HttpMethod.Put, $"{P(project)}/members/{Uri.EscapeDataString(userId)}", new { role }, ct: ct);

    public Task RemoveMemberAsync(string project, string userId, CancellationToken ct = default) =>
        SendOnlyAsync(HttpMethod.Delete, $"{P(project)}/members/{Uri.EscapeDataString(userId)}", ct: ct);

    public Task<List<UserInfo>> GetUsersAsync(CancellationToken ct = default) => ReadAsync<List<UserInfo>>(HttpMethod.Get, "api/v1/users", ct: ct);

    public Task CreateUserAsync(string email, string password, bool isInstanceAdmin, CancellationToken ct = default) =>
        SendOnlyAsync(HttpMethod.Post, "api/v1/users", new { email, password, isInstanceAdmin }, ct: ct);

    public Task DeleteUserAsync(string userId, CancellationToken ct = default) =>
        SendOnlyAsync(HttpMethod.Delete, $"api/v1/users/{Uri.EscapeDataString(userId)}", ct: ct);
}
