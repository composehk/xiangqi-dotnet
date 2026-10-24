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

// 断点续弈 (paid continue-from-position) — payment + persistence.
var paymentOptions = new Xiangqi.Server.Kaspa.PaymentOptions();
builder.Configuration.GetSection("Payment").Bind(paymentOptions);
if (paymentOptions.TestMode && builder.Environment.IsProduction())
    throw new InvalidOperationException("Payment:TestMode must never be enabled in Production.");
builder.Services.AddSingleton(paymentOptions);
string dbPath = builder.Configuration["Storage:DbPath"] is { Length: > 0 } configuredDb
    ? configuredDb
    : !string.IsNullOrWhiteSpace(testing)
        ? Path.Combine(Path.GetTempPath(), $"xiangqi-test-{Guid.NewGuid():N}.db")
        : "App_Data/xiangqi.db";
if (!Path.IsPathRooted(dbPath))
    dbPath = Path.Combine(builder.Environment.ContentRootPath, dbPath);
builder.Services.AddSingleton(new ContinueStore(dbPath));
if (paymentOptions.TestMode)
    builder.Services.AddSingleton<Xiangqi.Server.Kaspa.IPaymentVerifier, TestModePaymentVerifier>();
else
    builder.Services.AddHttpClient<Xiangqi.Server.Kaspa.IPaymentVerifier, Xiangqi.Server.Kaspa.KaspaPaymentVerifier>(c =>
        c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<ContinueService>();
builder.Services.AddSingleton<ArenaService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ArenaService>());

var app = builder.Build();

app.UseCors("open");
app.UseDefaultFiles();
app.UseStaticFiles();

var buildInfo = BuildInfo.FromAssembly(typeof(Program).Assembly);
app.MapGet("/api/health", () => Results.Ok(new HealthDto("ok", buildInfo.Version, buildInfo.BuiltAt)));

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

app.MapGet("/api/continue/config", (ContinueService svc) => Results.Ok(svc.Config()));

app.MapPost("/api/continue/parse", (ParseRecordRequest? request) =>
    ApiResults.Of(() => Results.Ok(ContinueService.ParseRecord(request))));

app.MapPost("/api/continue/sessions", (CreateContinueRequest? request, ContinueService svc) =>
    ApiResults.Of(() => Results.Ok(svc.Create(request))));

app.MapGet("/api/continue/sessions/{id}", (string id, ContinueService svc) =>
    ApiResults.Of(() => Results.Ok(svc.Get(id))));

app.MapPost("/api/continue/sessions/{id}/auth", (string id, ContinueAuthRequest? request, ContinueService svc) =>
    ApiResults.Of(() => Results.Ok(svc.Authorize(id, request))));

app.MapPost("/api/continue/sessions/{id}/payment", (string id, ContinuePaymentRequest? request, ContinueService svc, CancellationToken ct) =>
    ApiResults.OfAsync(async () =>
    {
        var (status, session) = await svc.SubmitPaymentAsync(id, request, ct);
        return Results.Json(session, statusCode: status);
    }));

app.MapPost("/api/continue/sessions/{id}/moves", (string id, ContinueMoveRequest? request, ContinueService svc, CancellationToken ct) =>
    ApiResults.OfAsync(async () => Results.Ok(await svc.PlayAsync(id, request, ct))));

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
