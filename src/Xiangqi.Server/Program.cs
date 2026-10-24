using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    static string NormalizeOrigin(string origin) => origin.Trim().TrimEnd('/');

    static bool IsLocalDevOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
            return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        string host = uri.IdnHost;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.Ordinal)
            || host.Equals("::1", StringComparison.Ordinal);
    }

    bool allowAnyOrigin = builder.Configuration.GetValue("Cors:AllowAnyOrigin", false);
    string[] configured = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
    var allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (string item in configured)
    {
        if (!string.IsNullOrWhiteSpace(item))
            allowedOrigins.Add(NormalizeOrigin(item));
    }

    options.AddPolicy("open", policy =>
    {
        if (allowAnyOrigin)
            policy.AllowAnyOrigin();
        else
            policy.SetIsOriginAllowed(origin =>
                !string.IsNullOrWhiteSpace(origin)
                && (allowedOrigins.Contains(NormalizeOrigin(origin)) || IsLocalDevOrigin(origin)));
        policy.AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services.AddSignalR();

// Rate limiting: per client IP, fixed one-minute window, POST only.
// Paid continue keeps its own partitions. Anonymous room create / vs-AI and room moves / join
// use ArenaOptions (0 or less disables that partition). Reads are not limited.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (context, ct) =>
    {
        int retryAfter = 60;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan wait))
            retryAfter = Math.Clamp((int)Math.Ceiling(wait.TotalSeconds), 1, 3600);
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await context.HttpContext.Response.WriteAsJsonAsync(
            new ErrorDto($"请求过于频繁，请 {retryAfter} 秒后再试 / too many requests, retry in {retryAfter}s"), ct);
    };
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var path = ctx.Request.Path;
        if (HttpMethods.IsPost(ctx.Request.Method) && path.StartsWithSegments("/api/continue"))
        {
            var limits = ctx.RequestServices.GetRequiredService<Xiangqi.Server.Kaspa.PaymentOptions>();
            bool create = path.Value is "/api/continue/sessions" or "/api/continue/sessions/";
            int permits = create ? limits.CreatePerMinute : limits.ActionPerMinute;
            if (permits <= 0)
                return RateLimitPartition.GetNoLimiter("off");
            string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter($"{(create ? "c" : "a")}:{ip}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        }

        if (HttpMethods.IsPost(ctx.Request.Method))
        {
            string normalized = path.Value ?? "";
            if (normalized.Length > 1 && normalized.EndsWith('/'))
                normalized = normalized.TrimEnd('/');
            bool createRoom = normalized is "/api/rooms" or "/api/games/vs-ai";
            bool roomAction = normalized.StartsWith("/api/rooms/", StringComparison.Ordinal)
                && (normalized.EndsWith("/moves", StringComparison.Ordinal) || normalized.EndsWith("/join", StringComparison.Ordinal));
            if (createRoom || roomAction)
            {
                var arena = ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<ArenaOptions>>().Value;
                int permits = createRoom ? arena.CreatePerMinute : arena.ActionPerMinute;
                if (permits <= 0)
                    return RateLimitPartition.GetNoLimiter("off");
                string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter($"{(createRoom ? "rc" : "ra")}:{ip}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            }
        }

        return RateLimitPartition.GetNoLimiter("none");
    });
});

// 断点续弈 (paid continue-from-position) — payment + persistence.
var paymentOptions = new Xiangqi.Server.Kaspa.PaymentOptions();
builder.Configuration.GetSection("Payment").Bind(paymentOptions);
// TestMode accepts ANY txid as paid. Only Development or the automated test host may enable it;
// every other environment name (Production, Staging, unset, typos) refuses to start.
if (paymentOptions.TestMode && !builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(testing))
    throw new InvalidOperationException("Payment:TestMode is only allowed in Development / automated tests.");
if (!paymentOptions.TestMode && paymentOptions.IsConfigured
    && !(Uri.TryCreate(paymentOptions.ApiBaseUrl, UriKind.Absolute, out var apiUri) && apiUri.Scheme == Uri.UriSchemeHttps))
    throw new InvalidOperationException("Payment:ApiBaseUrl must be an https URL.");
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
    {
        c.Timeout = TimeSpan.FromSeconds(15);
        c.MaxResponseContentBufferSize = 1 << 20; // a Kaspa tx lookup is a few KB; never buffer more than 1 MiB
    });
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ContinueService>();
builder.Services.Configure<ArenaOptions>(builder.Configuration.GetSection("Arena"));
builder.Services.AddSingleton<ArenaService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ArenaService>());

// Forwarded headers default to none.
// IIS in-process with direct clients: Connection.RemoteIpAddress is already the real client.
// Never trust X-Forwarded-For blindly. Honor X-Forwarded-For / X-Forwarded-Proto only when an
// operator opts in by listing the proxies (Forwarded:KnownProxies) or CIDR ranges
// (Forwarded:KnownNetworks) that are allowed to overwrite the client address. Anything else
// leaves the rate-limiter partition on the connection address, so a spoofed XFF cannot mint a new bucket.
string[]? knownProxies = builder.Configuration.GetSection("Forwarded:KnownProxies").Get<string[]>();
string[]? knownNetworks = builder.Configuration.GetSection("Forwarded:KnownNetworks").Get<string[]>();
bool honorForwarded = (knownProxies is { Length: > 0 }) || (knownNetworks is { Length: > 0 });
if (honorForwarded)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        // Drop the loopback defaults. An empty allow-list would trust every local proxy, including TestServer.
        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();
        if (knownProxies is not null)
        {
            foreach (string raw in knownProxies)
            {
                if (!IPAddress.TryParse(raw, out IPAddress? ip))
                    throw new InvalidOperationException($"Forwarded:KnownProxies entry '{raw}' is not a valid IP address.");
                options.KnownProxies.Add(ip);
            }
        }
        if (knownNetworks is not null)
        {
            foreach (string raw in knownNetworks)
            {
                string[] parts = raw.Split('/', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2
                    || !IPAddress.TryParse(parts[0], out IPAddress? prefix)
                    || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int prefixLength))
                    throw new InvalidOperationException($"Forwarded:KnownNetworks entry '{raw}' is not a valid CIDR (expected a.b.c.d/prefix).");
                try
                {
                    options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength));
                }
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    throw new InvalidOperationException($"Forwarded:KnownNetworks entry '{raw}' is not a valid CIDR (expected a.b.c.d/prefix).", ex);
                }
            }
        }
    });
}

var app = builder.Build();

if (honorForwarded)
    app.UseForwardedHeaders();

app.UseCors("open");

// Request-size cap for non-GET /api calls (a record + moves fits in a few KB).
const long MaxContinueBody = 64 * 1024;
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method))
    {
        if (ctx.Request.ContentLength > MaxContinueBody)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await ctx.Response.WriteAsJsonAsync(new ErrorDto("请求体过大 / request body too large"));
            return;
        }
        var feature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = MaxContinueBody; // also covers chunked bodies without Content-Length
    }
    await next();
});
app.UseRateLimiter();
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
