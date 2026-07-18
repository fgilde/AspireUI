using AspireUI.Server.Endpoints;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();       // serves built SPA from wwwroot (Task 10 copies it here)
app.MapStackEndpoints();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program { } // expose for WebApplicationFactory
