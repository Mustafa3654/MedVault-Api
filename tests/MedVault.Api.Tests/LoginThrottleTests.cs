using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MedVault.Api.Tests;

/// <summary>
/// The two login buckets, and the reason there are two.
///
/// MedVault proxies every patient in the country through one server, so partitioning on the client IP
/// alone put the entire patient population into a single ten-attempts-per-quarter-hour bucket - a busy
/// morning locked everyone out. Allowlisting that server fixes the outage; the per-account bucket is
/// what keeps the allowlist from also handing an attacker unlimited guesses.
/// </summary>
public sealed class LoginThrottleTests
{
    [Fact]
    public async Task An_allowlisted_client_is_not_throttled_by_the_shared_ip_bucket()
    {
        using var factory = new ThrottleFactory(clientIp: "10.9.0.5", trustedIp: "10.9.0.5", ipLimit: 2, userLimit: 100);
        var client = factory.CreateClient();

        // Well past the per-IP limit. Each is a different patient arriving through the same server,
        // which is exactly the traffic the old partitioning could not tell apart from an attack.
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, await LoginAs(client, $"7020{i:D4}"));
    }

    [Fact]
    public async Task An_allowlisted_client_is_still_throttled_per_account()
    {
        using var factory = new ThrottleFactory(clientIp: "10.9.0.5", trustedIp: "10.9.0.5", ipLimit: 100, userLimit: 3);
        var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) statuses.Add(await LoginAs(client, "00000000"));

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK,
             HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests],
            statuses);
    }

    /// <summary>
    /// One patient exhausting their own budget must not cost the next patient anything, or the
    /// account bucket would just be the IP bucket wearing a different key.
    /// </summary>
    [Fact]
    public async Task Exhausting_one_account_does_not_affect_another()
    {
        using var factory = new ThrottleFactory(clientIp: "10.9.0.5", trustedIp: "10.9.0.5", ipLimit: 100, userLimit: 2);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await LoginAs(client, "00000000"));
        Assert.Equal(HttpStatusCode.OK, await LoginAs(client, "00000000"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAs(client, "00000000"));

        Assert.Equal(HttpStatusCode.OK, await LoginAs(client, "70999999"));
    }

    /// <summary>Not being on the list must still cost the caller its IP budget.</summary>
    [Fact]
    public async Task An_unlisted_client_keeps_the_per_ip_bucket()
    {
        using var factory = new ThrottleFactory(clientIp: "198.51.100.7", trustedIp: "10.9.0.5", ipLimit: 2, userLimit: 100);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await LoginAs(client, "70200001"));
        Assert.Equal(HttpStatusCode.OK, await LoginAs(client, "70200002"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAs(client, "70200003"));
    }

    /// <summary>
    /// Buffering the body to read the username must leave it readable by the endpoint, or every login
    /// would arrive with nothing bound.
    /// </summary>
    [Fact]
    public async Task Reading_the_username_for_partitioning_leaves_the_body_intact()
    {
        using var factory = new ThrottleFactory(clientIp: "10.9.0.5", trustedIp: "10.9.0.5", ipLimit: 100, userLimit: 100);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/mobile/login",
            new { username = "00000000", password = "medvault", type = "P" });

        var json = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.True(json["success"]!.GetValue<bool>());
        Assert.Equal("00000000", json["user"]!.GetValue<string>());
    }

    /// <summary>A body the limiter cannot read a username from is still the endpoint's 400 to report.</summary>
    [Fact]
    public async Task A_malformed_login_body_is_still_a_bad_request()
    {
        using var factory = new ThrottleFactory(clientIp: "10.9.0.5", trustedIp: "10.9.0.5", ipLimit: 100, userLimit: 100);
        var client = factory.CreateClient();

        var response = await client.PostAsync("/api/mobile/login",
            new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<HttpStatusCode> LoginAs(HttpClient client, string username) =>
        (await client.PostAsJsonAsync("/api/mobile/login",
            new { username, password = "medvault", type = "P" })).StatusCode;

    // ---------- host ----------

    /// <summary>TestServer leaves Connection.RemoteIpAddress null; the allowlist needs one to match.</summary>
    private sealed class RemoteIpFilter(string ip) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nextMiddleware) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                await nextMiddleware();
            });

            next(app);
        };
    }

    private sealed class ThrottleFactory(string clientIp, string trustedIp, int ipLimit, int userLimit)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("His:Provider", "mock");
            builder.UseSetting("Auth:RequirePassword", "false");
            builder.UseSetting("Auth:TrustedClientIps:0", trustedIp);
            builder.UseSetting("Auth:LoginAttemptsPerWindow", ipLimit.ToString());
            builder.UseSetting("Auth:LoginAttemptsPerUserPerWindow", userLimit.ToString());
            builder.UseSetting("Auth:LoginWindowMinutes", "15");

            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(new RemoteIpFilter(clientIp)));
        }
    }
}
