using Bunting.Admin.Api;
using Bunting.Admin.Infrastructure;
using Bunting.Domain;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Bunting.Admin.Tests.Support;

/// <summary>A bUnit context with MudBlazor, the fake API and the services the pages need.</summary>
public sealed class AdminTestContext : BunitContext
{
    public AdminTestContext(ProjectRole? role = ProjectRole.Owner, bool instanceAdmin = false)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Api = new FakeApi { Role = role, InstanceAdmin = instanceAdmin };
        Services.AddSingleton(Api);
        Services.AddSingleton<IBuntingApi>(Api);
        Services.AddScoped<CurrentUser>();
        Services.AddScoped<UiGuard>();

        Render<MudPopoverProvider>();
        Dialogs = Render<MudDialogProvider>();
        Render<MudSnackbarProvider>();
    }

    public FakeApi Api { get; }

    public IRenderedComponent<MudDialogProvider> Dialogs { get; }

    /// <summary>Renders a page or component inside <c>ProjectShell</c>-like cascading context, for components that need one.</summary>
    public IRenderedComponent<T> RenderWithProject<T>(Action<ComponentParameterCollectionBuilder<T>>? parameters = null)
        where T : Microsoft.AspNetCore.Components.IComponent =>
        Render<T>(p =>
        {
            p.AddCascadingValue(new ProjectContext(FakeApi.ProjectInfoValue, Api.Role));
            parameters?.Invoke(p);
        });
}
