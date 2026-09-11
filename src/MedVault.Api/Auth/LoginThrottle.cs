using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace MedVault.Api.Auth;

/// <summary>
/// Login throttling, in two independent buckets.
///
/// Partitioning on the client IP alone was the original design, and it is still what stops someone
/// walking the mobile-number space to discover which numbers belong to patients (spec section 9,
/// rule 5). But MedVault does not call this API from patient handsets - every patient in the country
/// arrives through one MedVault server, so one IP bucket is the whole patient population sharing ten
/// attempts per quarter hour, and a busy morning locks all of them out.
///
/// So the IP bucket stays for unknown callers, an allowlist exempts the MedVault server from it, and
/// a second bucket per username applies to everyone - including the allowlisted server. The account
/// bucket is the one that actually protects an individual patient from being guessed at, and it does
/// not care how many other patients are behind the same address.
/// </summary>
public static class LoginThrottle
{
    public const string PolicyName = "login";

    private const string UsernameItemKey = "medvault.login.username";
    private const string LoginPath = "/api/mobile/login";

    /// <summary>Bodies larger than this are not searched for a username - a login request is ~80 bytes.</summary>
    private const int MaxBufferedBody = 8 * 1024;

    /// <summary>
    /// The rate limiter runs before model binding, so the username is not available to it yet. This
    /// middleware buffers the login body once, lifts the username out and rewinds the stream, leaving
    /// the endpoint to bind it normally.
    /// </summary>
    public static async Task CaptureUsernameAsync(HttpContext ctx, RequestDelegate next)
    {
        if (HttpMethods.IsPost(ctx.Request.Method) &&
            ctx.Request.Path.StartsWithSegments(LoginPath, StringComparison.OrdinalIgnoreCase) &&
            ctx.Request.ContentLength is null or <= MaxBufferedBody)
        {
            ctx.Request.EnableBuffering(bufferThreshold: MaxBufferedBody, bufferLimit: MaxBufferedBody);
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("username", out var username) &&
                    username.ValueKind == JsonValueKind.String)
                {
                    ctx.Items[UsernameItemKey] = username.GetString();
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
            {
                // A malformed or oversized body is the endpoint's problem to report as a 400. Here it
                // just means no username to partition on, and the request falls into the shared bucket.
            }

            ctx.Request.Body.Position = 0;
        }

        await next(ctx);
    }

    /// <summary>
    /// Per-account bucket. Applies to every caller, allowlisted or not: the point is to make guessing
    /// one patient's number slow, and the trusted server must not be a way around that.
    /// </summary>
    public static RateLimitPartition<string> PerUsername(HttpContext ctx, IConfiguration config)
    {
        // Requests with no readable username share one bucket rather than escaping the limit.
        var username = ctx.Items.TryGetValue(UsernameItemKey, out var value) && value is string { Length: > 0 } u
            ? u.Trim().ToUpperInvariant()
            : "(none)";

        // Defaults to the per-IP limit so a deployment that only ever set the one knob keeps the
        // number it chose, and so the two buckets cannot silently disagree.
        var perIpLimit = config.GetValue("Auth:LoginAttemptsPerWindow", 10);

        return RateLimitPartition.GetFixedWindowLimiter($"user:{username}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("Auth:LoginAttemptsPerUserPerWindow", perIpLimit),
            Window = TimeSpan.FromMinutes(config.GetValue("Auth:LoginWindowMinutes", 15)),
        });
    }

    /// <summary>
    /// Per-source bucket, skipped for allowlisted addresses. Only reaches the login path - every other
    /// endpoint gets a no-op partition, because this runs as the global limiter.
    /// </summary>
    public static RateLimitPartition<string> PerClientIp(HttpContext ctx, IConfiguration config, IReadOnlySet<IPAddress> trusted)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method) ||
            !ctx.Request.Path.StartsWithSegments(LoginPath, StringComparison.OrdinalIgnoreCase))
            return RateLimitPartition.GetNoLimiter("");

        var address = ctx.Connection.RemoteIpAddress;

        // Compared as IPAddress, not as text: ::ffff:10.0.0.5 and 10.0.0.5 are the same host, and
        // whether the API sees the mapped form depends on how the server socket was bound.
        if (address is not null && trusted.Contains(Unmap(address)))
            return RateLimitPartition.GetNoLimiter("");

        return RateLimitPartition.GetFixedWindowLimiter($"ip:{address?.ToString() ?? "unknown"}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = config.GetValue("Auth:LoginAttemptsPerWindow", 10),
                Window = TimeSpan.FromMinutes(config.GetValue("Auth:LoginWindowMinutes", 15)),
            });
    }

    /// <summary>
    /// Parses Auth:TrustedClientIps. A bad entry is fatal on purpose: silently dropping one would
    /// leave the integration throttled with nothing in the log to say why.
    /// </summary>
    public static IReadOnlySet<IPAddress> ReadTrustedIps(IConfiguration config)
    {
        var configured = config.GetSection("Auth:TrustedClientIps").Get<string[]>() ?? [];

        return configured
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => Unmap(IPAddress.Parse(entry.Trim())))
            .ToHashSet();
    }

    private static IPAddress Unmap(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
