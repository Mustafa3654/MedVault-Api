using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using MedVault.Api.Auth;
using MedVault.Api.Contracts;
using MedVault.Api.Data;
using MedVault.Api.Endpoints;
using MedVault.Api.Files;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    // Nulls must be written: the app distinguishes null from "" (spec section 2).
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;

    // The relaxed encoder leaves "&" and "<" as themselves, so responses are byte-comparable with the
    // MedVault captures during acceptance testing. Both encoders produce the same decoded strings, so the
    // mobile app cannot tell the difference. Switch back to the default encoder if any consumer ever
    // injects these responses straight into an HTML page.
    o.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

// Minimal-API body binding reports a malformed body by throwing only when ThrowOnBadRequest is set,
// and its default is "true in Development, false everywhere else". Left at the default, the hospital
// server answers every unparseable login with a bare 400 and an empty body - the one shape the mobile
// app's parser has never seen, and the reason a mistyped Postman body was indistinguishable from a
// rejected login. Forced on so the exception handler below returns the same ErrorResponse in every
// environment, and so the tests (which host as Development) exercise the behaviour the server has.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITokenService, InMemoryTokenService>();
builder.Services.AddSingleton<PdfFileResolver>();

// Reference ranges are not in the HIS at all; they come from a catalogue file. Singleton because it
// is read once at startup - editing the catalogue needs a restart.
builder.Services.AddSingleton<ReferenceRanges>();

// "mock" serves the MedVault captures from Data/Seed; "sql" reads the real HIS.
var provider = builder.Configuration["His:Provider"] ?? "mock";
if (string.Equals(provider, "sql", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddScoped<IHisRepository, SqlHisRepository>();
else
    builder.Services.AddSingleton<IHisRepository, MockHisRepository>();

// The login limiter partitions on the client IP, so it only works if the IP we see is the client's.
// Behind IIS/ARR or any other reverse proxy every request arrives with the proxy's address, which
// would put the whole hospital into one shared bucket - the eleventh login of the day would be
// throttled. Network:KnownProxies fixes that by trusting X-Forwarded-For, but ONLY from the listed
// addresses: trusting the header unconditionally lets a caller spoof it and skip the limit entirely.
// Empty (the default) means "not behind a proxy" and leaves Connection.RemoteIpAddress alone.
var knownProxies = builder.Configuration.GetSection("Network:KnownProxies").Get<string[]>() ?? [];
if (knownProxies.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        o.ForwardLimit = builder.Configuration.GetValue("Network:ForwardLimit", 1);
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        foreach (var proxy in knownProxies) o.KnownProxies.Add(IPAddress.Parse(proxy));
    });
}

// Slows down enumeration of which mobile numbers are registered patients (spec section 9, rule 5),
// without putting every patient behind the MedVault server into one shared bucket - see LoginThrottle.
var trustedClientIps = LoginThrottle.ReadTrustedIps(builder.Configuration);

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Every other error in this API is an ErrorResponse; a bare status code with no body would be the
    // one shape the mobile app's parser has never seen.
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.HttpContext.Response.WriteAsJsonAsync(
            new ErrorResponse { Message = "Too many attempts" }, ct);
    };

    // Endpoint policy: one bucket per account, for every caller.
    o.AddPolicy(LoginThrottle.PolicyName, ctx => LoginThrottle.PerUsername(ctx, builder.Configuration));

    // Global limiter: one bucket per source address, skipped on the login path for allowlisted
    // addresses and a no-op everywhere else. It runs alongside the policy above, so an untrusted
    // caller has to satisfy both.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
        ctx => LoginThrottle.PerClientIp(ctx, builder.Configuration, trustedClientIps));
});

var app = builder.Build();

if (knownProxies.Length > 0) app.UseForwardedHeaders();

// Never let a SQL error, stack trace, or table name reach the client (spec section 8).
app.UseExceptionHandler(branch => branch.Run(async ctx =>
{
    var error = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;

    // A body that is not valid JSON surfaces here as an exception rather than at the endpoint, and
    // reporting it as 503 would tell the app the hospital server is down when the request was simply
    // malformed - and would log a stack trace for every one. It is a client error: say 400 and move on.
    if (IsMalformedRequest(error))
    {
        app.Logger.LogDebug(error, "Malformed request on {Path}", ctx.Request.Path);
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new ErrorResponse { Message = "Bad request" });
        return;
    }

    app.Logger.LogError(error, "Unhandled error on {Path} (trace {TraceId})", ctx.Request.Path, ctx.TraceIdentifier);

    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    await ctx.Response.WriteAsJsonAsync(new ErrorResponse { Message = "Service unavailable" });
}));

// FoxPro 9 writes every generated report into Files:Root (C:\ExcelH); this serves that folder as-is
// under /ResultPDF, which is the prefix pdf_path carries. Registered only when the folder exists, so
// a machine without it still starts - PhysicalFileProvider throws on a missing directory.
//
// WARNING: this route has no token and no ownership check. Anyone who can reach the port and guess a
// filename can read another patient's report, which is exactly what PdfFileResolver.Resolve and the
// /api/mobile/file route exist to prevent. Fine while the app is bound to localhost:5000; put the
// folder behind /api/mobile/file, or gate it with a middleware, before exposing this to the network.
{
    var pdfs = app.Services.GetRequiredService<PdfFileResolver>();
    if (pdfs.CreateFileProvider() is { } pdfFiles)
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = pdfFiles,
            RequestPath = "/" + PdfFileResolver.UrlPrefix,

            // .pdf is a known type. Leaving this false means anything else FoxPro drops in the folder
            // is not served at all, rather than served as an unknown blob.
            ServeUnknownFileTypes = false,
        });
    }
    else
    {
        app.Logger.LogWarning(
            "Files:Root is not set or does not exist - /{Prefix} is not being served and every pdf_path will be null.",
            PdfFileResolver.UrlPrefix);
    }
}

// Must run before the rate limiter: it is what makes the username available to partition on.
app.Use(LoginThrottle.CaptureUsernameAsync);

app.UseRateLimiter();

app.MapMobileEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// The model binder wraps a JsonException in a BadHttpRequestException; check both so a future
// framework change that stops wrapping does not quietly turn 400s back into 503s.
static bool IsMalformedRequest(Exception? error) =>
    error is BadHttpRequestException or JsonException || error?.InnerException is JsonException;

public partial class Program;   // exposed so the test project can host it in-process
