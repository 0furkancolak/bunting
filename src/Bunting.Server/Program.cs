
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("bunting");

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { name = "Bunting", status = "ok" }));
app.MapDefaultEndpoints();

app.Run();

/// <summary>Entry point marker for WebApplicationFactory.</summary>
public partial class Program;
