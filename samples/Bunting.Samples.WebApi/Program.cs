var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// The SDK integration arrives in Phase 5.
app.MapGet("/", () => "Bunting sample API");

app.Run();
