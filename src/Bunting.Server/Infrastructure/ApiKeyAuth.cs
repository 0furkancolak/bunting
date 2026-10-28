using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Bunting.Domain;
using Bunting.Server.Features.ApiKeys;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Bunting.Server.Infrastructure;

public static class ApiKeyPolicies
{
    public const string Scheme = "BuntingApiKey";
    public const string AnyKey = "api-key";
    public const string ServerKey = "server-key";
    public const string RateLimit = "api-key";
    public const string KeyTypeClaim = "bunting_key_type";
    public const string EnvironmentClaim = "bunting_env";
    public const string KeyIdClaim = "bunting_key_id";

    public static void AddApiKeyAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitOptions>(configuration.GetSection("Bunting:RateLimit"));
        services.AddMemoryCache();
        services.AddSingleton<ApiKeyService>();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>(Scheme, null);
        services.AddAuthorizationBuilder()
            .AddPolicy(AnyKey, p => p.AddAuthenticationSchemes(Scheme).RequireClaim(KeyTypeClaim))
            .AddPolicy(ServerKey, p => p.AddAuthenticationSchemes(Scheme).RequireClaim(KeyTypeClaim, nameof(ApiKeyType.Server)));
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (context, _) =>
            {
                var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
                context.HttpContext.Response.Headers.RetryAfter = options.WindowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };

            // Partition by the presented key (hashed, so the secret is never used as a dictionary key); anonymous traffic by IP.
            o.AddPolicy(RateLimit, context =>
            {
                var options = context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
                var presented = ApiKeyAuthHandler.ReadKey(context.Request);
                var partition = presented is null
                    ? "ip:" + context.Connection.RemoteIpAddress
                    : "key:" + Convert.ToHexString(ApiKeyService.Hash(presented))[..16];
                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = presented is null ? Math.Min(options.PermitLimit, 60) : options.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.WindowSeconds),
                    QueueLimit = 0,
                });
            });
        });
    }
}

public sealed class RateLimitOptions
{
    /// <summary>Requests allowed per API key within <see cref="WindowSeconds"/>.</summary>
    public int PermitLimit { get; set; } = 6000;

    public int WindowSeconds { get; set; } = 1;
}

internal sealed class ApiKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyService keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Reads the key from <c>X-API-Key</c> or <c>Authorization: Bearer bnt_…</c> (the two OFREP mechanisms).</summary>
    public static string? ReadKey(HttpRequest request)
    {
        var header = request.Headers["X-API-Key"].ToString();
        if (ApiKeyService.LooksLikeApiKey(header))
        {
            return header;
        }

        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authorization["Bearer ".Length..].Trim();
            return ApiKeyService.LooksLikeApiKey(token) ? token : null;
        }

        return null;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = ReadKey(Request);
        if (presented is null)
        {
            return AuthenticateResult.NoResult();
        }

        var info = await keys.ValidateAsync(presented, Context.RequestAborted);
        if (info is null)
        {
            return AuthenticateResult.Fail("Invalid or revoked API key.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ApiKeyPolicies.KeyTypeClaim, info.Type.ToString()),
                new Claim(ApiKeyPolicies.EnvironmentClaim, info.EnvironmentId.ToString()),
                new Claim(ApiKeyPolicies.KeyIdClaim, info.KeyId.ToString()),
            ],
            Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
