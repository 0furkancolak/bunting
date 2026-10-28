using System.Text.RegularExpressions;
using Bunting.Domain;
using Bunting.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Bunting.Server.Infrastructure;

public static partial class Lookups
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex ScopeKeyRegex();

    /// <summary>Project and environment keys: lowercase alphanumerics and dashes, up to 63 characters.</summary>
    public static bool IsValidScopeKey(string? key) => key is not null && ScopeKeyRegex().IsMatch(key);

    public static async Task<Project> RequireProjectAsync(this BuntingDbContext db, string key, CancellationToken ct) =>
        await db.Projects.SingleOrDefaultAsync(p => p.Key == key, ct)
        ?? throw Problems.Throw(Problems.NotFound($"Project '{key}' was not found."));

    public static async Task<ProjectEnvironment> RequireEnvironmentAsync(
        this BuntingDbContext db, Guid projectId, string key, CancellationToken ct) =>
        await db.Environments.SingleOrDefaultAsync(e => e.ProjectId == projectId && e.Key == key, ct)
        ?? throw Problems.Throw(Problems.NotFound($"Environment '{key}' was not found."));

    public static async Task<Flag> RequireFlagAsync(this BuntingDbContext db, Guid projectId, string key, CancellationToken ct) =>
        await db.Flags.SingleOrDefaultAsync(f => f.ProjectId == projectId && f.Key == key, ct)
        ?? throw Problems.Throw(Problems.NotFound($"Flag '{key}' was not found."));
}
