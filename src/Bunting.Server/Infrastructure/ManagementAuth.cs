using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Bunting.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Bunting.Server.Infrastructure;

public static class ManagementPolicies
{
    public const string Scheme = "BuntingToken";
    public const string RoleClaim = "bunting_role";
    public const string Viewer = "viewer";
    public const string Editor = "editor";
    public const string Owner = "owner";

    public static void AddManagementAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ManagementOptions>(configuration.GetSection("Bunting:Management"));
        services.AddAuthentication(Scheme).AddScheme<AuthenticationSchemeOptions, TokenAuthHandler>(Scheme, null);
        services.AddAuthorizationBuilder()
            .AddPolicy(Viewer, p => p.RequireAuthenticatedUser().RequireAssertion(c => HasRole(c.User, ProjectRole.Viewer)))
            .AddPolicy(Editor, p => p.RequireAuthenticatedUser().RequireAssertion(c => HasRole(c.User, ProjectRole.Editor)))
            .AddPolicy(Owner, p => p.RequireAuthenticatedUser().RequireAssertion(c => HasRole(c.User, ProjectRole.Owner)));
    }

    private static bool HasRole(ClaimsPrincipal user, ProjectRole required) =>
        Enum.TryParse<ProjectRole>(user.FindFirstValue(RoleClaim), out var role) && role >= required;
}

public sealed class ManagementOptions
{
    /// <summary>Static bearer tokens for the Management API. Replaced/extended by Identity cookies in the admin phase.</summary>
    public List<ManagementToken> Tokens { get; set; } = [];
}

public sealed class ManagementToken
{
    public string Name { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public ProjectRole Role { get; set; } = ProjectRole.Viewer;
}

internal sealed class TokenAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<ManagementOptions> management)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim()));
        ManagementToken? match = null;
        foreach (var token in management.CurrentValue.Tokens)
        {
            // Compare hashes in constant time; do not short-circuit so timing does not reveal which token matched.
            var expected = SHA256.HashData(Encoding.UTF8.GetBytes(token.Token));
            if (CryptographicOperations.FixedTimeEquals(presented, expected) && token.Token.Length > 0)
            {
                match = token;
            }
        }

        if (match is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid token."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, match.Name), new Claim(ManagementPolicies.RoleClaim, match.Role.ToString())],
            Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
