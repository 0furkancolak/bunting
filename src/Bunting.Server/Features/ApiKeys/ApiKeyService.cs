using System.Security.Cryptography;
using System.Text;
using Bunting.Domain;
using Bunting.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Bunting.Server.Features.ApiKeys;

/// <summary>Identity of a validated API key.</summary>
public sealed record ApiKeyPrincipalInfo(Guid KeyId, Guid EnvironmentId, Guid ProjectId, ApiKeyType Type);

/// <summary>Generates, hashes and validates API keys. Only SHA-256 hashes are persisted.</summary>
public sealed class ApiKeyService(IServiceScopeFactory scopes, IMemoryCache cache)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    public static (string Plaintext, byte[] Hash, string Hint) Generate(ApiKeyType type)
    {
        var prefix = type == ApiKeyType.Server ? ApiKey.ServerPrefix : ApiKey.ClientPrefix;
        var plaintext = prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        return (plaintext, Hash(plaintext), plaintext[^4..]);
    }

    public static byte[] Hash(string plaintext) => SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));

    public static bool LooksLikeApiKey(string? value) =>
        value is not null && (value.StartsWith(ApiKey.ServerPrefix, StringComparison.Ordinal) || value.StartsWith(ApiKey.ClientPrefix, StringComparison.Ordinal));

    /// <summary>Returns the key's identity, or <see langword="null"/> when unknown or revoked.</summary>
    public async Task<ApiKeyPrincipalInfo?> ValidateAsync(string plaintext, CancellationToken ct)
    {
        if (!LooksLikeApiKey(plaintext))
        {
            return null;
        }

        var hash = Hash(plaintext);
        var cacheKey = "apikey:" + Convert.ToHexString(hash);
        if (cache.TryGetValue(cacheKey, out ApiKeyPrincipalInfo? cached))
        {
            return cached;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BuntingDbContext>();

        // The unique index finds the row by hash; the explicit constant-time comparison keeps the check independent of the
        // database's comparison semantics.
        var candidate = await db.ApiKeys.AsNoTracking()
            .Where(k => k.KeyHash == hash && k.RevokedAt == null)
            .Select(k => new { k.Id, k.EnvironmentId, k.Type, k.KeyHash })
            .SingleOrDefaultAsync(ct);
        ApiKeyPrincipalInfo? result = null;
        if (candidate is not null && CryptographicOperations.FixedTimeEquals(candidate.KeyHash, hash))
        {
            var projectId = await db.Environments.AsNoTracking()
                .Where(e => e.Id == candidate.EnvironmentId).Select(e => e.ProjectId).SingleAsync(ct);
            result = new ApiKeyPrincipalInfo(candidate.Id, candidate.EnvironmentId, projectId, candidate.Type);
        }

        // Negative results are cached too, so invalid keys cannot be used to hammer the database.
        cache.Set(cacheKey, result, CacheTtl);
        return result;
    }

    /// <summary>Drops cached validations so a revocation takes effect immediately on this instance.</summary>
    public void Forget(byte[] hash) => cache.Remove("apikey:" + Convert.ToHexString(hash));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
