using System.Collections.Concurrent;
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
string[] configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
var allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (string item in configuredOrigins)
{
    if (!string.IsNullOrWhiteSpace(item))
        allowedOrigins.Add(NormalizeOrigin(item));
}

// Shared by the "open" CORS policy and the /hub/game Origin check.
bool IsAllowlistedOrigin(string? origin) =>
    !string.IsNullOrWhiteSpace(origin)
    && (allowedOrigins.Contains(NormalizeOrigin(origin)) || IsLocalDevOrigin(origin));

builder.Services.AddCors(options =>
{
    options.AddPolicy("open", policy =>
    {
        if (allowAnyOrigin)
            policy.AllowAnyOrigin();
        else
            policy.SetIsOriginAllowed(origin => IsAllowlistedOrigin(origin));
        policy.AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services.AddSignalR();

// Rate limiting: per client IP, fixed one-minute window, POST only.
// Paid continue keeps its own partitions. Anonymous room create / vs-AI and room moves / join
// use ArenaOptions (0 or less disables that partition). Reads are not limited.
// FixedWindow's RetryAfter metadata is the full window, not the time left.
// windowStarts records, per partition key, when the current window was first seen.
TimeSpan rateWindow = TimeSpan.FromMinutes(1);
int rateWindowSeconds = (int)rateWindow.TotalSeconds;
var windowStarts = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

// null when this request is not counted against a fixed window (same rules as the limiter below).
static (string Key, int Permits)? ClassifyRateLimit(HttpContext ctx)
{
    var path = ctx.Request.Path;
    if (!HttpMethods.IsPost(ctx.Request.Method))
        return null;

    if (path.StartsWithSegments("/api/continue"))
    {
        var limits = ctx.RequestServices.GetRequiredService<Xiangqi.Server.Kaspa.PaymentOptions>();
        bool create = path.Value is "/api/continue/sessions" or "/api/continue/sessions/";
        int permits = create ? limits.CreatePerMinute : limits.ActionPerMinute;
        if (permits <= 0)
            return null;
        string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return ($"{(create ? "c" : "a")}:{ip}", permits);
    }

    string normalized = path.Value ?? "";
    if (normalized.Length > 1 && normalized.EndsWith('/'))
        normalized = normalized.TrimEnd('/');
    bool createRoom = normalized is "/api/rooms" or "/api/games/vs-ai";
    bool roomAction = normalized.StartsWith("/api/rooms/", StringComparison.Ordinal)
        && (normalized.EndsWith("/moves", StringComparison.Ordinal)
            || normalized.EndsWith("/join", StringComparison.Ordinal)
            || normalized.EndsWith("/resign", StringComparison.Ordinal)
            || normalized.EndsWith("/undo", StringComparison.Ordinal));
    if (!createRoom && !roomAction)
        return null;

    var arena = ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<ArenaOptions>>().Value;
    int arenaPermits = createRoom ? arena.CreatePerMinute : arena.ActionPerMinute;
    if (arenaPermits <= 0)
        return null;
    string clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    return ($"{(createRoom ? "rc" : "ra")}:{clientIp}", arenaPermits);
}

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (context, ct) =>
    {
        int retryAfter = rateWindowSeconds;
        (string Key, int Permits)? partition = ClassifyRateLimit(context.HttpContext);
        if (partition is not null && windowStarts.TryGetValue(partition.Value.Key, out DateTime started))
        {
            double secondsLeft = (started + rateWindow - DateTime.UtcNow).TotalSeconds;
            retryAfter = (int)Math.Ceiling(secondsLeft);
        }
        // Never advertise a longer wait than the window, and never 0.
        retryAfter = Math.Clamp(retryAfter, 1, rateWindowSeconds);
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await context.HttpContext.Response.WriteAsJsonAsync(
            new ErrorDto($"请求过于频繁，请 {retryAfter} 秒后再试 / too many requests, retry in {retryAfter}s"), ct);
    };
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        (string Key, int Permits)? partition = ClassifyRateLimit(ctx);
        if (partition is null)
            return RateLimitPartition.GetNoLimiter("none");
        int permits = partition.Value.Permits;
        return RateLimitPartition.GetFixedWindowLimiter(partition.Value.Key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = rateWindow,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
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

// Kestrel's default Server header advertises the stack. Leave it off; the header middleware also never sets X-Powered-By.
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

var app = builder.Build();

if (honorForwarded)
    app.UseForwardedHeaders();

// OnStarting runs when the response starts, including static files, API errors, and 429/413 short-circuits.
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        var headers = ctx.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Frame-Options"] = "DENY";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        // CSP connect-src includes the request Host; caches must vary on it.
        headers.AppendCommaSeparatedValues("Vary", "Host");
        string connectSrc = "connect-src 'self'";
        string? hostValue = ctx.Request.Host.Value;
        if (IsSafeCspHost(hostValue))
        {
            string wsOrigin = ctx.Request.IsHttps ? $"wss://{hostValue}" : $"ws://{hostValue}";
            connectSrc += " " + wsOrigin;
        }
        string? connectSrcExtra = app.Configuration["Csp:ConnectSrcExtra"];
        if (!string.IsNullOrWhiteSpace(connectSrcExtra))
        {
            string extra = string.Join(' ', connectSrcExtra.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (extra.Length > 0)
                connectSrc += " " + extra;
        }
        headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self' data:; "
            + connectSrc
            + "; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
        if (ctx.Request.IsHttps)
            headers["Strict-Transport-Security"] = "max-age=15552000";
        return Task.CompletedTask;
    });
    await next();
});

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

// Remember when each limiter partition's current window started, before UseRateLimiter acquires a permit.
// FixedWindow itself only reports the full window length in Retry-After metadata.
const int MaxWindowStartEntries = 8192;
app.Use(async (ctx, next) =>
{
    (string Key, int Permits)? partition = ClassifyRateLimit(ctx);
    if (partition is not null)
    {
        DateTime now = DateTime.UtcNow;
        windowStarts.AddOrUpdate(
            partition.Value.Key,
            now,
            (_, existing) => now - existing >= rateWindow ? now : existing);
        if (windowStarts.Count > 256)
        {
            DateTime cutoff = now - rateWindow;
            foreach (KeyValuePair<string, DateTime> entry in windowStarts)
            {
                if (entry.Value < cutoff)
                    windowStarts.TryRemove(entry.Key, out _);
            }
            // Still over the cap (many distinct keys inside one window): drop oldest extras.
            if (windowStarts.Count > MaxWindowStartEntries)
            {
                foreach (string stale in windowStarts.OrderBy(pair => pair.Value).Select(pair => pair.Key).Take(windowStarts.Count - MaxWindowStartEntries).ToArray())
                    windowStarts.TryRemove(stale, out _);
            }
        }
    }
    await next();
});
app.UseRateLimiter();

// Invalid room ids never match {id:guid}, so routing would otherwise answer 404 with an empty body.
// A well-formed but unknown id still falls through and returns the service's bilingual 404.
app.Use(async (ctx, next) =>
{
    const string roomsPrefix = "/api/rooms/";
    string path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith(roomsPrefix, StringComparison.OrdinalIgnoreCase))
    {
        string rest = path[roomsPrefix.Length..];
        int slash = rest.IndexOf('/');
        string segment = slash >= 0 ? rest[..slash] : rest;
        if (segment.Length > 0 && !Guid.TryParse(segment, out _))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new ErrorDto("房间编号无效 / Invalid room id."));
            return;
        }
    }

    try
    {
        await next();
    }
    catch (BadHttpRequestException) when (!ctx.Response.HasStarted && ctx.Request.Path.StartsWithSegments("/api"))
    {
        // Development throws the binding failure instead of answering 400; keep both environments uniform
        // and never echo parser internals back to the client.
        ctx.Response.Clear();
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new ErrorDto("请求格式无效 / Malformed request body."));
        return;
    }

    // Minimal APIs answer malformed JSON / bad content type with 400 or 415 and an empty body.
    // Unmatched /api routes and wrong verbs are empty 404 / 405. Leave any response that already
    // wrote JSON (or anything else) untouched — including a service 404 for an unknown room.
    if (!ctx.Request.Path.StartsWithSegments("/api"))
        return;
    if (ctx.Response.StatusCode is not (
        StatusCodes.Status400BadRequest
        or StatusCodes.Status415UnsupportedMediaType
        or StatusCodes.Status404NotFound
        or StatusCodes.Status405MethodNotAllowed))
        return;
    if (ctx.Response.HasStarted
        || ctx.Response.ContentLength is not null
        || !string.IsNullOrEmpty(ctx.Response.ContentType))
        return;
    string emptyBodyError = ctx.Response.StatusCode switch
    {
        StatusCodes.Status404NotFound => "接口不存在 / Not found.",
        StatusCodes.Status405MethodNotAllowed => "不支持的请求方法 / Method not allowed.",
        _ => "请求格式无效 / Malformed request body.",
    };
    await ctx.Response.WriteAsJsonAsync(new ErrorDto(emptyBodyError));
});

// Browsers send Origin on negotiate and on the WebSocket upgrade. Non-browser clients omit it.
// Allow same-origin, the CORS allowlist, and local dev origins; anything else is 403.
// Registered with the other middleware, before any Map* call, so it runs before the hub endpoint.
app.Use(async (ctx, next) =>
{
    string origin = ctx.Request.Headers.Origin.ToString();
    if (ctx.Request.Path.StartsWithSegments("/hub/game")
        && !string.IsNullOrWhiteSpace(origin))
    {
        bool sameOrigin = false;
        if (Uri.TryCreate(origin, UriKind.Absolute, out Uri? originUri)
            && string.Equals(originUri.Scheme, ctx.Request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(originUri.IdnHost, ctx.Request.Host.Host, StringComparison.OrdinalIgnoreCase))
        {
            int originPort = originUri.Port;
            int requestPort = ctx.Request.Host.Port ?? (ctx.Request.IsHttps ? 443 : 80);
            sameOrigin = originPort == requestPort;
        }

        if (!sameOrigin && !allowAnyOrigin && !IsAllowlistedOrigin(origin))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new ErrorDto("来源不被允许 / Origin not allowed."));
            return;
        }
    }

    await next();
});

app.UseDefaultFiles();
app.MapMethods("/favicon.ico", new[] { HttpMethods.Get, HttpMethods.Head }, (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "public, max-age=86400";
    return Results.File(FaviconIco.Bytes, "image/x-icon");
}).ExcludeFromDescription();
app.UseStaticFiles();

var buildInfo = BuildInfo.FromAssembly(typeof(Program).Assembly);
app.MapGet("/api/health", () => Results.Ok(new HealthDto("ok", buildInfo.Version, buildInfo.BuiltAt)));

app.MapGet("/api/rooms", (ArenaService arena) =>
    ApiResults.Of(() => Results.Ok(arena.List())));

app.MapGet("/api/rooms/{id:guid}", (Guid id, ArenaService arena, string? quiet) =>
    ApiResults.Of(() =>
    {
        bool isQuiet = string.Equals(quiet, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(quiet, "true", StringComparison.OrdinalIgnoreCase);
        try
        {
            var room = arena.Get(id);
            if (room is null)
            {
                if (isQuiet)
                    return Results.Json(new { gone = true, error = "房间不存在 / Room not found." });
                return Results.Json(new ErrorDto("房间不存在 / Room not found."), statusCode: StatusCodes.Status404NotFound);
            }
            return Results.Ok(room);
        }
        catch (ApiException ex) when (isQuiet && ex.Status == StatusCodes.Status404NotFound)
        {
            return Results.Json(new { gone = true, error = ex.Message });
        }
    }));

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

app.MapPost("/api/rooms/{id:guid}/resign", (Guid id, TokenRequest? request, ArenaService arena) =>
    ApiResults.OfAsync(async () => Results.Ok(await arena.ResignAsync(id, request))));

app.MapPost("/api/rooms/{id:guid}/undo", (Guid id, TokenRequest? request, ArenaService arena) =>
    ApiResults.OfAsync(async () => Results.Ok(await arena.UndoAsync(id, request))));

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

static bool IsSafeCspHost(string? host)
{
    if (string.IsNullOrEmpty(host) || host.Length > 255)
        return false;
    foreach (char c in host)
    {
        if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
            or '.' or '-' or ':' or '[' or ']')
            continue;
        return false;
    }
    return true;
}

public partial class Program
{
}
