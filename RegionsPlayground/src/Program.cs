using System.Text.Json.Serialization;
using Regions.Playground;
using Regions.Simulator;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
var app = builder.Build();

// Local only: the sandbox has no authentication and is meant for the developer's own machine.
if (app.Urls.Count == 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    app.Urls.Add("http://localhost:5280");

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/scenarios", () => Scenarios.All.Select(e => new { e.Name, e.Description }));
app.MapGet("/api/scenarios/{name}", (string name) =>
{
    try { return Results.Ok(Playground.FromScenario(Scenarios.Build(name))); }
    catch (ArgumentException ex) { return Results.NotFound(ex.Message); }
});
app.MapGet("/api/empty", () => Playground.Empty());
app.MapPost("/api/analyze", (PlaygroundModel model) => Playground.Analyze(model));

app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine($"Regions Sandbox is running. Open {string.Join(", ", app.Urls)} in your browser. Press Ctrl+C to stop."));

app.Run();

/// <summary>Exposed for tests.</summary>
public partial class Program { }
