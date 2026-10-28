using Bunting.Admin.Api;
using Bunting.Admin.Components;
using Bunting.Admin.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddUiLocalization();
builder.Services.AddCascadingAuthenticationState();

// In a container the default key ring would be lost on restart and sign everyone out; keep it on a volume.
if (builder.Configuration["Bunting:DataProtectionPath"] is { Length: > 0 } keyPath)
{
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyPath)).SetApplicationName("Bunting.Admin");
}

builder.Services.AddAuthorization();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Cookie.Name = "bunting.admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(11); // shorter than the lifetime of the server's bearer token
        options.SlidingExpiration = false;
    });

// The Admin has no database: everything goes through the server's Management API.
var serverUrl = new Uri(
    builder.Configuration["Bunting:ServerUrl"]
    ?? builder.Configuration["services:server:http:0"]
    ?? "http://localhost:5080");
builder.Services.AddHttpClient("bunting-server", client => client.BaseAddress = serverUrl);
builder.Services.AddHttpClient<IBuntingApi, BuntingApi>(client => client.BaseAddress = serverUrl);
builder.Services.AddScoped<IAccessTokenProvider, ClaimsAccessTokenProvider>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<UiGuard>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseRequestLocalization();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapDefaultEndpoints();
app.MapCultureEndpoint();
app.MapStaticAssets();

app.MapPost("/account/login", async (HttpContext http, [FromForm] LoginForm form, IHttpClientFactory clients) =>
{
    try
    {
        var client = clients.CreateClient("bunting-server");
        var token = await BuntingApi.LoginAsync(client, form.Email ?? string.Empty, form.Password ?? string.Empty, http.RequestAborted);
        var me = await new BuntingApi(client, new StaticToken(token)).GetMeAsync(http.RequestAborted);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, ClaimsAccessTokenProvider.CreatePrincipal(me.Name, token));
        return Results.LocalRedirect(IsLocal(form.ReturnUrl) ? form.ReturnUrl! : "/");
    }
    catch (ApiException e) when (e.IsUnauthorized)
    {
        return Results.LocalRedirect("/login?error=credentials");
    }
    catch (HttpRequestException)
    {
        return Results.LocalRedirect("/login?error=server");
    }
});

app.MapPost("/account/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login");
});

// Used when the server rejected the stored token (expired): clear the cookie and sign in again.
app.MapGet("/account/expired", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login?error=expired");
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

static bool IsLocal(string? url) =>
    !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));

/// <summary>Entry point marker for WebApplicationFactory.</summary>
public partial class Program;

internal sealed class LoginForm
{
    public string? Email { get; set; }

    public string? Password { get; set; }

    public string? ReturnUrl { get; set; }
}
