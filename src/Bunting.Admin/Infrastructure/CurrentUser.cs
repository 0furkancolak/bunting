using System.Security.Claims;
using Bunting.Admin.Api;
using Bunting.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;

namespace Bunting.Admin.Infrastructure;

internal sealed class StaticToken(string token) : IAccessTokenProvider
{
    public ValueTask<string?> GetTokenAsync() => ValueTask.FromResult<string?>(token);
}

/// <summary>Reads the Management API token that the login endpoint stored in the sign-in cookie.</summary>
public sealed class ClaimsAccessTokenProvider(AuthenticationStateProvider authentication) : IAccessTokenProvider
{
    public const string TokenClaim = "bunting_token";

    public static ClaimsPrincipal CreatePrincipal(string name, string token) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name), new Claim(TokenClaim, token)], CookieAuthenticationDefaults.AuthenticationScheme));

    public async ValueTask<string?> GetTokenAsync() =>
        (await authentication.GetAuthenticationStateAsync()).User.FindFirstValue(TokenClaim);
}

/// <summary>The signed-in user's identity and roles, loaded once per circuit.</summary>
public sealed class CurrentUser(IBuntingApi api)
{
    private MeInfo? _me;

    public async Task<MeInfo> GetAsync(CancellationToken ct = default) => _me ??= await api.GetMeAsync(ct);

    public void Reset() => _me = null;

    public async Task<ProjectRole?> RoleInAsync(string project, CancellationToken ct = default) => (await GetAsync(ct)).RoleIn(project);
}

/// <summary>The project a page works in, with the caller's role. Controls hide write actions; the server enforces them.</summary>
public sealed record ProjectContext(ProjectInfo Project, ProjectRole? Role)
{
    public bool CanEdit => Role >= ProjectRole.Editor;

    public bool IsOwner => Role >= ProjectRole.Owner;
}

/// <summary>Runs API calls from pages and turns failures into messages (and expired sessions into a new sign-in).</summary>
public sealed class UiGuard(ISnackbar snackbar, Microsoft.AspNetCore.Components.NavigationManager navigation)
{
    /// <summary>Runs <paramref name="action"/>; returns whether it succeeded.</summary>
    public async Task<bool> RunAsync(Func<Task> action, string? success = null)
    {
        try
        {
            await action();
            if (success is not null)
            {
                snackbar.Add(success, Severity.Success);
            }

            return true;
        }
        catch (ApiException e)
        {
            Report(e);
            return false;
        }
        catch (HttpRequestException)
        {
            snackbar.Add("The Bunting server cannot be reached.", Severity.Error);
            return false;
        }
    }

    public async Task<T?> LoadAsync<T>(Func<Task<T>> load)
    {
        T? result = default;
        await RunAsync(async () => result = await load());
        return result;
    }

    public void Report(ApiException e)
    {
        if (e.IsUnauthorized)
        {
            navigation.NavigateTo("/account/expired", forceLoad: true);
            return;
        }

        var text = e.IsConflict && e.CurrentVersion is not null
            ? "Someone else changed this in the meantime. Reload to see the latest version."
            : e.Status == System.Net.HttpStatusCode.Forbidden
                ? "Your role does not allow this."
                : e.Message;
        snackbar.Add(text, Severity.Error);
    }
}
