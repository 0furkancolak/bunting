using Bunting.Server.Data;
using Bunting.Server.Features.Audit;
using Bunting.Server.Features.Environments;
using Bunting.Server.Features.FlagConfigs;
using Bunting.Server.Features.Flags;
using Bunting.Server.Features.Projects;
using Bunting.Server.Features.Rulesets;
using Bunting.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("bunting");
builder.Services.AddDbContext<BuntingDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ChangeLog>();
builder.Services.AddScoped<RulesetSnapshotBuilder>();
builder.Services.AddManagementAuth(builder.Configuration);
builder.Services.AddProblemDetails();

var app = builder.Build();

if (app.Configuration.GetValue("Bunting:Database:AutoMigrate", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<BuntingDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { name = "Bunting", status = "ok" }));
app.MapDefaultEndpoints();

var api = app.MapGroup("/api/v1");
ProjectEndpoints.Map(api);
EnvironmentEndpoints.Map(api);
FlagEndpoints.Map(api);
FlagConfigEndpoints.Map(api);
Bunting.Server.Features.Segments.SegmentEndpoints.Map(api);
AuditEndpoints.Map(api);

app.Run();

/// <summary>Entry point marker for WebApplicationFactory.</summary>
public partial class Program;
