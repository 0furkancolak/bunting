using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace Bunting.Admin.Infrastructure;

/// <summary>The languages the admin UI is translated into, and the endpoint that remembers the visitor's choice.</summary>
public static class UiCultures
{
    public static readonly string[] Supported = ["en", "tr"];

    public const string Default = "en";

    public static IServiceCollection AddUiLocalization(this IServiceCollection services)
    {
        services.AddLocalization();
        services.Configure<RequestLocalizationOptions>(options =>
        {
            options.SetDefaultCulture(Default).AddSupportedCultures(Supported).AddSupportedUICultures(Supported);
        });
        return services;
    }

    public static void MapCultureEndpoint(this IEndpointRouteBuilder app) =>
        app.MapGet("/culture/{culture}", (HttpContext http, string culture, string? redirect) =>
        {
            if (Array.IndexOf(Supported, culture) >= 0)
            {
                http.Response.Cookies.Append(
                    CookieRequestCultureProvider.DefaultCookieName,
                    CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                    new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax, HttpOnly = false });
            }

            return Results.LocalRedirect(redirect is { Length: > 0 } && redirect.StartsWith('/') && !redirect.StartsWith("//", StringComparison.Ordinal) ? redirect : "/");
        }).AllowAnonymous();

    public static string Current => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
}
