using System.Text.Json;
using Xiangqi.Server;

var builder = WebApplication.CreateBuilder(args);

// Default listen address is http://0.0.0.0:5080.
// dotnet run (launchSettings), Docker (ASPNETCORE_URLS), and an explicit
// ASPNETCORE_HTTP_PORTS win. API tests set XIANGQI_TESTING and keep TestServer.
string? testing = Environment.GetEnvironmentVariable("XIANGQI_TESTING");
string? urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
string? httpPorts = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
if (string.IsNullOrWhiteSpace(testing)
    && string.IsNullOrWhiteSpace(urls)
    && string.IsNullOrWhiteSpace(httpPorts))
{
    builder.WebHost.UseUrls("http://0.0.0.0:5080");
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("open", policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddSignalR();
builder.Services.AddSingleton<ArenaService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ArenaService>());

var app = builder.Build();

app.UseCors("open");
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new HealthDto("ok")));

app.MapGet("/api/rooms", (ArenaService arena) =>
    ApiResults.Of(() => Results.Ok(arena.List())));

app.MapGet("/api/rooms/{id:guid}", (Guid id, ArenaService arena) =>
    ApiResults.Of(() => Results.Ok(arena.Get(id))));

app.MapGet("/api/rooms/{id:guid}/moves", (Guid id, ArenaService arena) =>
    ApiResults.Of(() => Results.Ok(arena.LegalMoves(id))));

app.MapPost("/api/rooms", (CreateRoomRequest? request, ArenaService arena) =>
    ApiResults.Of(() => Results.Ok(arena.CreateRoom(request))));

app.MapPost("/api/games/vs-ai", (VsAiRequest? request, ArenaService arena) =>
    ApiResults.Of(() => Results.Ok(arena.CreateVsAi(request))));

app.MapPost("/api/rooms/{id:guid}/moves", (Guid id, MoveRequest? request, ArenaService arena) =>
    ApiResults.OfAsync(async () => Results.Ok(await arena.PlayAsync(id, request))));

app.MapPost("/api/rooms/{id:guid}/join", (Guid id, JoinRequest? request, ArenaService arena) =>
    ApiResults.OfAsync(async () => Results.Ok(await arena.JoinAsync(id, request))));

app.MapHub<GameHub>("/hub/game").RequireCors("open");

app.Lifetime.ApplicationStarted.Register(() =>
{
    string listening = app.Urls.Count > 0 ? string.Join(", ", app.Urls) : "http://0.0.0.0:5080";
    app.Logger.LogInformation("Xiangqi arena ready at {Urls}", listening);
});

app.Run();

public partial class Program
{
}
