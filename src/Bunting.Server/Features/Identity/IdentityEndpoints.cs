using System.Security.Claims;
using Bunting.Domain;
using Bunting.Server.Data;
using Bunting.Server.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Bunting.Server.Features.Identity;

public sealed record LoginRequest(string Email, string Password);

public sealed record CreateUserRequest(string Email, string Password, bool IsInstanceAdmin = false);

public sealed record UserDto(string Id, string Email, bool IsInstanceAdmin);

public sealed record MembershipDto(string ProjectKey, ProjectRole Role);

public sealed record MeDto(string Name, string? Email, bool IsInstanceAdmin, ProjectRole? GlobalRole, IReadOnlyList<MembershipDto> Memberships);

public sealed record AddMemberRequest(string Email, ProjectRole Role);

public sealed record UpdateMemberRequest(ProjectRole Role);

public sealed record MemberDto(string UserId, string Email, ProjectRole Role);

public static class IdentityEndpoints
{
    public static void AddBuntingIdentity(this IServiceCollection services)
    {
        services.AddIdentityCore<BuntingUser>(options =>
            {
                options.Password.RequiredLength = 10;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.User.RequireUniqueEmail = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddEntityFrameworkStores<BuntingDbContext>();

        // Keys live in the database so that bearer tokens are valid on every server instance and survive restarts.
        services.AddDataProtection().PersistKeysToDbContext<BuntingDbContext>().SetApplicationName("Bunting");
    }

    public static void Map(RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");
        auth.MapPost("/login", Login).AllowAnonymous();
        auth.MapGet("/me", Me).RequireAuthorization(ManagementPolicies.Viewer);

        var users = api.MapGroup("/users").RequireAuthorization(ManagementPolicies.Owner);
        users.MapGet("/", ListUsers);
        users.MapPost("/", CreateUser);
        users.MapDelete("/{userId}", DeleteUser);

        var members = api.MapGroup("/projects/{projectKey}/members").RequireAuthorization(ManagementPolicies.Owner);
        members.MapGet("/", ListMembers);
        members.MapPost("/", AddMember);
        members.MapPut("/{userId}", UpdateMember);
        members.MapDelete("/{userId}", RemoveMember);
    }

    private static IResult Unauthorized() =>
        Results.Problem(detail: "Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");

    private static async Task<IResult> Login(LoginRequest request, UserManager<BuntingUser> users)
    {
        var user = string.IsNullOrWhiteSpace(request.Email) ? null : await users.FindByEmailAsync(request.Email);
        if (user is null)
        {
            // Spend the same time as a real check so that unknown emails are not distinguishable by latency.
            users.PasswordHasher.HashPassword(new BuntingUser(), request.Password ?? string.Empty);
            return Unauthorized();
        }

        if (await users.IsLockedOutAsync(user) || !await users.CheckPasswordAsync(user, request.Password ?? string.Empty))
        {
            await users.AccessFailedAsync(user);
            return Unauthorized();
        }

        await users.ResetAccessFailedCountAsync(user);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Email!),
        };
        if (user.IsInstanceAdmin)
        {
            claims.Add(new Claim(ManagementPolicies.GlobalRoleClaim, nameof(ProjectRole.Owner)));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, IdentityConstants.BearerScheme));
        return Results.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
    }

    private static async Task<IResult> Me(HttpContext http, BuntingDbContext db, CancellationToken ct)
    {
        var principal = http.User;
        var global = Enum.TryParse<ProjectRole>(principal.FindFirstValue(ManagementPolicies.GlobalRoleClaim), out var role) ? role : (ProjectRole?)null;
        var memberships = new List<MembershipDto>();
        if (principal.UserId() is { } id)
        {
            var rows = await db.ProjectMembers.AsNoTracking()
                .Where(m => m.UserId == id)
                .Join(db.Projects, m => m.ProjectId, p => p.Id, (m, p) => new { p.Key, m.Role })
                .OrderBy(x => x.Key)
                .ToListAsync(ct);
            memberships = [.. rows.Select(x => new MembershipDto(x.Key, x.Role))];
        }

        // Static tokens carry a global role but no user; instance administrators are users with the global owner role.
        var isAdmin = global == ProjectRole.Owner;
        return Results.Ok(new MeDto(principal.Identity?.Name ?? "unknown", principal.Identity?.Name, isAdmin, global, memberships));
    }

    private static async Task<IResult> ListUsers(UserManager<BuntingUser> users, CancellationToken ct) =>
        Results.Ok(await users.Users.AsNoTracking().OrderBy(u => u.Email).Select(u => new UserDto(u.Id, u.Email!, u.IsInstanceAdmin)).ToListAsync(ct));

    private static async Task<IResult> CreateUser(CreateUserRequest request, UserManager<BuntingUser> users)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@', StringComparison.Ordinal))
        {
            return Problems.Validation("email", "A valid email address is required.");
        }

        var user = new BuntingUser { Id = Guid.NewGuid().ToString(), UserName = request.Email.Trim(), Email = request.Email.Trim(), IsInstanceAdmin = request.IsInstanceAdmin };
        var result = await users.CreateAsync(user, request.Password ?? string.Empty);
        if (!result.Succeeded)
        {
            return Problems.Validation(result.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
        }

        return Results.Created($"/api/v1/users/{user.Id}", new UserDto(user.Id, user.Email, user.IsInstanceAdmin));
    }

    private static async Task<IResult> DeleteUser(string userId, UserManager<BuntingUser> users, HttpContext http)
    {
        if (http.User.UserId() == userId)
        {
            return Problems.Conflict("You cannot delete your own account.");
        }

        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            return Problems.NotFound("User was not found.");
        }

        await users.DeleteAsync(user);
        return Results.NoContent();
    }

    private static async Task<IResult> ListMembers(string projectKey, BuntingDbContext db, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Key == projectKey, ct);
        if (project is null)
        {
            return Problems.NotFound($"Project '{projectKey}' was not found.");
        }

        var members = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == project.Id)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { u.Id, u.Email, m.Role })
            .OrderBy(x => x.Email)
            .ToListAsync(ct);
        return Results.Ok(members.Select(x => new MemberDto(x.Id, x.Email!, x.Role)));
    }

    private static Task<IResult> AddMember(string projectKey, AddMemberRequest request, BuntingDbContext db, UserManager<BuntingUser> users, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                var project = await db.RequireProjectAsync(projectKey, ct);
                var user = string.IsNullOrWhiteSpace(request.Email) ? null : await users.FindByEmailAsync(request.Email.Trim());
                if (user is null)
                {
                    throw Problems.Throw(Problems.Validation("email", "No user with this email exists."));
                }

                if (!Enum.IsDefined(request.Role))
                {
                    throw Problems.Throw(Problems.Validation("role", "Unknown role."));
                }

                if (await db.ProjectMembers.AnyAsync(m => m.ProjectId == project.Id && m.UserId == user.Id, ct))
                {
                    throw Problems.Throw(Problems.Conflict("The user is already a member of this project."));
                }

                db.ProjectMembers.Add(new ProjectMember { ProjectId = project.Id, UserId = user.Id, Role = request.Role });
                log.Audit(project.Id, null, "member", user.Email!, "added", null, new { user.Email, request.Role });
                return Results.Created($"/api/v1/projects/{projectKey}/members/{user.Id}", new MemberDto(user.Id, user.Email!, request.Role));
            },
            ct);

    private static Task<IResult> UpdateMember(string projectKey, string userId, UpdateMemberRequest request, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                var (project, member, email) = await RequireMember(db, projectKey, userId, ct);
                if (!Enum.IsDefined(request.Role))
                {
                    throw Problems.Throw(Problems.Validation("role", "Unknown role."));
                }

                var before = new { Role = member.Role };
                member.Role = request.Role;
                log.Audit(project.Id, null, "member", email, "updated", before, new { request.Role });
                return Results.Ok(new MemberDto(userId, email, request.Role));
            },
            ct);

    private static Task<IResult> RemoveMember(string projectKey, string userId, BuntingDbContext db, ChangeLog log, CancellationToken ct) =>
        log.WriteAsync(
            async () =>
            {
                var (project, member, email) = await RequireMember(db, projectKey, userId, ct);
                db.ProjectMembers.Remove(member);
                log.Audit(project.Id, null, "member", email, "removed", new { member.Role }, null);
                return Results.NoContent();
            },
            ct);

    private static async Task<(Project Project, ProjectMember Member, string Email)> RequireMember(BuntingDbContext db, string projectKey, string userId, CancellationToken ct)
    {
        var project = await db.RequireProjectAsync(projectKey, ct);
        var member = await db.ProjectMembers.SingleOrDefaultAsync(m => m.ProjectId == project.Id && m.UserId == userId, ct)
            ?? throw Problems.Throw(Problems.NotFound("The user is not a member of this project."));
        var email = await db.Users.Where(u => u.Id == userId).Select(u => u.Email!).SingleAsync(ct);
        return (project, member, email);
    }
}

/// <summary>Creates the first instance administrator from configuration when no user with that email exists.</summary>
public sealed partial class BootstrapAdmin(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<BootstrapAdmin> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var email = configuration["Bunting:Bootstrap:AdminEmail"];
        var password = configuration["Bunting:Bootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        if (configuration.GetValue("Bunting:Database:AutoMigrate", true))
        {
            await scope.ServiceProvider.GetRequiredService<BuntingDbContext>().Database.MigrateAsync(cancellationToken);
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<BuntingUser>>();
        if (await users.FindByEmailAsync(email) is not null)
        {
            return;
        }

        var result = await users.CreateAsync(
            new BuntingUser { Id = Guid.NewGuid().ToString(), UserName = email, Email = email, IsInstanceAdmin = true },
            password);
        if (result.Succeeded)
        {
            LogCreated(logger, email);
        }
        else
        {
            LogFailed(logger, string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the bootstrap administrator {Email}")]
    private static partial void LogCreated(ILogger logger, string email);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not create the bootstrap administrator: {Errors}")]
    private static partial void LogFailed(ILogger logger, string errors);
}
