using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Bunting.Domain;
using Bunting.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bunting.Server.Infrastructure;

public static class ManagementPolicies
{
    public const string Scheme = "BuntingToken";
    public const string RoleClaim = "bunting_role";
    public const string Viewer = "viewer";
    public const string Editor = "editor";
    public const string Owner = "owner";

    /// <summary>Claim set on principals that hold a role for the whole instance (static tokens and instance administrators).</summary>
    public const string GlobalRoleClaim = RoleClaim;

    public static void AddManagementAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ManagementOptions>(configuration.GetSection("Bunting:Management"));
        services.AddAuthentication(Scheme)
            .AddScheme<AuthenticationSchemeOptions, TokenAuthHandler>(Scheme, null)
            .AddBearerToken(IdentityConstants.BearerScheme, options => options.BearerTokenExpiration = TimeSpan.FromHours(12));
        services.AddScoped<IAuthorizationHandler, ProjectRoleHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(Viewer, p => RequireRole(p, ProjectRole.Viewer))
            .AddPolicy(Editor, p => RequireRole(p, ProjectRole.Editor))
            .AddPolicy(Owner, p => RequireRole(p, ProjectRole.Owner));
    }

    private static void RequireRole(AuthorizationPolicyBuilder policy, ProjectRole role) =>
        policy.AddAuthenticationSchemes(Scheme, IdentityConstants.BearerScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new ProjectRoleRequirement(role));
}

public sealed record ProjectRoleRequirement(ProjectRole Role) : IAuthorizationRequirement;

/// <summary>
/// Static tokens and instance administrators hold one role for every project. Other users hold the role of their
/// <see cref="ProjectMember"/> row for the project named by the <c>projectKey</c> route value. Routes without a project
/// (listing projects, current user) admit any authenticated user at viewer level; the endpoint filters its output.
/// </summary>
internal sealed class ProjectRoleHandler(BuntingDbContext db) : AuthorizationHandler<ProjectRoleRequirement, HttpContext>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ProjectRoleRequirement requirement, HttpContext http)
    {
        var user = context.User;
        if (Enum.TryParse<ProjectRole>(user.FindFirstValue(ManagementPolicies.GlobalRoleClaim), out var global) && global >= requirement.Role)
        {
            context.Succeed(requirement);
            return;
        }

        if (user.FindFirstValue(ClaimTypes.NameIdentifier) is not { } userId)
        {
            return;
        }

        if (http.Request.RouteValues["projectKey"] is not string projectKey)
        {
            if (requirement.Role == ProjectRole.Viewer)
            {
                context.Succeed(requirement);
            }

            return;
        }

        var role = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.UserId == userId && db.Projects.Any(p => p.Id == m.ProjectId && p.Key == projectKey))
            .Select(m => (ProjectRole?)m.Role)
            .SingleOrDefaultAsync();
        if (role is { } actual && actual >= requirement.Role)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>Helpers for endpoints that need to know who is calling.</summary>
public static class Callers
{
    public static bool IsGlobal(this ClaimsPrincipal user) => user.FindFirstValue(ManagementPolicies.GlobalRoleClaim) is not null;

    public static string? UserId(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);
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
