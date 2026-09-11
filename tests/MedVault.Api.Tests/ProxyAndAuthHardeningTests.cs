using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MedVault.Api.Contracts;
using MedVault.Api.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MedVault.Api.Tests;

/// <summary>
/// Covers the three behaviours that the contract suite cannot reach: the endpoint's null-vs-empty
/// rule independently of which repository is plugged in, the reverse-proxy client-IP handling that
/// login throttling depends on, and the constant-time login path.
/// </summary>
public sealed class ProxyAndAuthHardeningTests
{
    private const string Mobile = "00000000";

    // ================= null vs empty, for any repository =================

    /// <summary>
    /// The pending-order fix lives in SqlHisRepository, which needs a real HIS database, so no test
    /// can exercise it directly. What can be pinned is the rule it has to obey - and pinning it at
    /// the endpoint, against a stub, is what makes the two implementations answerable to one contract
    /// instead of each drifting on its own.
    /// </summary>
    [Theory]
    [InlineData(true, HttpStatusCode.OK)]           // owned, no validated rows yet -> empty list
    [InlineData(false, HttpStatusCode.NotFound)]    // unknown or not this account's -> 404
    public async Task An_empty_result_list_is_200_and_a_null_one_is_404(bool ownedButPending, HttpStatusCode expected)
    {
        using var factory = new StubFactory(new StubRepository
        {
            Results = ownedButPending ? [] : null,
        });

        var client = factory.CreateClient();
        var token = await LoginAsync(client);

        var response = await client.GetAsync($"/api/mobile/results?requestnb=2026001002&tcToken={token}");
        Assert.Equal(expected, response.StatusCode);

        if (expected is not HttpStatusCode.OK) return;

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("2026001002", json["request_nb"]!.GetValue<string>());
        Assert.Empty(json["results"]!.AsArray());
    }

    // ================= implausible family groups =================

    /// <summary>
    /// The Bekaa HIS has one mobile number linked to 11,989 patient records - a placeholder, not a
    /// family. With Auth:RequirePassword=false the number alone logs in, so that number would be a key
    /// to all 11,989 sets of results. Refused, not truncated: handing back the first dozen would still
    /// be handing a stranger real patients' reports.
    /// </summary>
    [Theory]
    [InlineData(3, true)]      // a real family - the MedVault capture has three
    [InlineData(12, true)]     // at the limit
    [InlineData(13, false)]    // over it
    [InlineData(11989, false)] // the placeholder found in the live database
    public async Task A_number_linked_to_too_many_patients_cannot_log_in(int linkedPatients, bool expectSuccess)
    {
        using var factory = new StubFactory(new StubRepository
        {
            LinkedPatientIds = Enumerable.Range(0, linkedPatients).Select(i => $"PATIENT{i}").ToList(),
        });

        var response = await factory.CreateClient().PostAsJsonAsync("/api/mobile/login",
            new { username = Mobile, password = "medvault", type = "P" });

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(expectSuccess, json["success"]!.GetValue<bool>());

        if (!expectSuccess)
            Assert.Equal("Invalid credentials", json["message"]!.GetValue<string>());
    }

    /// <summary>The cap is a safety net, not a policy - a deployment that knows better can switch it off.</summary>
    [Fact]
    public async Task The_family_group_cap_can_be_disabled()
    {
        using var factory = new StubFactory(new StubRepository
        {
            LinkedPatientIds = Enumerable.Range(0, 500).Select(i => $"PATIENT{i}").ToList(),
        },
        maxLinkedPatients: 0);

        var response = await factory.CreateClient().PostAsJsonAsync("/api/mobile/login",
            new { username = Mobile, password = "medvault", type = "P" });

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.True(json["success"]!.GetValue<bool>());
    }

    // ================= reverse-proxy client IP =================

    /// <summary>
    /// With the proxy trusted, two clients behind it must get their own throttling budgets. If they
    /// did not, the limiter would reject the whole hospital once any ten logins had happened.
    /// </summary>
    [Fact]
    public async Task Clients_behind_a_trusted_proxy_are_throttled_independently()
    {
        using var factory = new ProxyFactory(proxyIp: "10.0.0.1", trustedProxy: "10.0.0.1", limit: 2);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await LoginFrom(client, "203.0.113.10"));
        Assert.Equal(HttpStatusCode.OK, await LoginFrom(client, "203.0.113.10"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginFrom(client, "203.0.113.10"));

        // A different patient, same proxy: unaffected by the first one exhausting its window.
        Assert.Equal(HttpStatusCode.OK, await LoginFrom(client, "203.0.113.99"));
    }

    /// <summary>
    /// The other half of the same setting. A caller that is not a listed proxy must not be able to
    /// mint a fresh throttling budget per request by inventing an X-Forwarded-For - that would turn
    /// the limiter off for exactly the attacker it exists to slow down.
    /// </summary>
    [Fact]
    public async Task A_spoofed_forwarded_header_from_an_untrusted_source_buys_no_extra_attempts()
    {
        using var factory = new ProxyFactory(proxyIp: "198.51.100.7", trustedProxy: "10.0.0.1", limit: 2);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await LoginFrom(client, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.OK, await LoginFrom(client, "203.0.113.2"));

        // Every request claims a brand new client, and every one of them is still the same real caller.
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginFrom(client, "203.0.113.3"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginFrom(client, "203.0.113.4"));
    }

    /// <summary>The default - no proxy configured - must keep the header ignored entirely.</summary>
    [Fact]
    public async Task Forwarded_headers_are_ignored_when_no_proxy_is_configured()
    {
        using var factory = new ProxyFactory(proxyIp: "10.0.0.1", trustedProxy: null, limit: 2);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await LoginFrom(client, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.OK, await LoginFrom(client, "203.0.113.2"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginFrom(client, "203.0.113.3"));
    }

    // ================= constant-time login =================

    /// <summary>
    /// An unknown mobile number must cost the same work as a known one, or the shared "Invalid
    /// credentials" message is undone by a stopwatch. Asserts the floor rather than comparing the two
    /// timings: PBKDF2 at 210,000 iterations cannot complete in single-digit milliseconds, so a
    /// response that fast is proof the verification was skipped.
    /// </summary>
    [Fact]
    public async Task An_unknown_mobile_number_still_pays_the_password_hashing_cost()
    {
        using var factory = new StubFactory(new StubRepository { AccountExists = false });
        var client = factory.CreateClient();

        // Warm up: first call pays JIT and the one-off DummyHash derivation.
        await Attempt();

        var elapsed = await Attempt();

        Assert.True(elapsed.TotalMilliseconds > 20,
            $"Unknown-account login returned in {elapsed.TotalMilliseconds:F1}ms, which is too fast for " +
            "PBKDF2 - the verification was short-circuited and login timing now reveals which mobile " +
            "numbers are registered patients.");

        async Task<TimeSpan> Attempt()
        {
            var started = Stopwatch.GetTimestamp();
            var response = await client.PostAsJsonAsync("/api/mobile/login",
                new { username = "70999999", password = "guess", type = "P" });
            var taken = Stopwatch.GetElapsedTime(started);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return taken;
        }
    }

    // ================= helpers =================

    private static async Task<string> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/mobile/login",
            new { username = Mobile, password = "medvault", type = "P" });

        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();
    }

    private static int _loginSequence;

    /// <summary>
    /// Every call uses a fresh username on purpose. Login is throttled in two buckets - per source IP
    /// and per account - and these tests are about the first one, so the account bucket must never be
    /// what rejects a request here or they would pass without the proxy handling working at all.
    /// </summary>
    private static async Task<HttpStatusCode> LoginFrom(HttpClient client, string clientIp)
    {
        var username = $"7010{Interlocked.Increment(ref _loginSequence):D4}";

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/mobile/login")
        {
            Content = JsonContent.Create(new { username, password = "medvault", type = "P" }),
        };
        request.Headers.Add("X-Forwarded-For", clientIp);

        return (await client.SendAsync(request)).StatusCode;
    }

    // ---------- hosts ----------

    /// <summary>
    /// TestServer leaves Connection.RemoteIpAddress null, so the forwarded-headers middleware has no
    /// address to match against KnownProxies. This sets one before the app's own pipeline runs, which
    /// is the only way to stand in for "a request that arrived from the proxy".
    /// </summary>
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

    private sealed class ProxyFactory(string proxyIp, string? trustedProxy, int limit)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("His:Provider", "mock");
            builder.UseSetting("Auth:RequirePassword", "false");
            builder.UseSetting("Auth:LoginAttemptsPerWindow", limit.ToString());
            builder.UseSetting("Auth:LoginWindowMinutes", "15");

            if (trustedProxy is not null)
                builder.UseSetting("Network:KnownProxies:0", trustedProxy);

            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(new RemoteIpFilter(proxyIp)));
        }
    }

    private sealed class StubFactory(StubRepository repository, int? maxLinkedPatients = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("His:Provider", "mock");
            builder.UseSetting("Auth:RequirePassword", "true");
            builder.UseSetting("Auth:LoginAttemptsPerWindow", "1000");

            if (maxLinkedPatients is { } max)
                builder.UseSetting("Auth:MaxLinkedPatients", max.ToString());

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHisRepository>();
                services.AddSingleton<IHisRepository>(repository);
            });
        }
    }

    /// <summary>Answers whatever the test needs it to, so the endpoint's own rules are what is measured.</summary>
    private sealed class StubRepository : IHisRepository
    {
        public IReadOnlyList<ResultItem>? Results { get; init; } = [];

        /// <summary>False stands in for "no such mobile number".</summary>
        public bool AccountExists { get; init; } = true;

        /// <summary>The family group behind the number. One patient unless a test says otherwise.</summary>
        public IReadOnlyList<string> LinkedPatientIds { get; init; } = ["ALEX  EXAMPLE"];

        private HisAccount? Account => AccountExists
            ? field ??= new(Mobile, "ALEX EXAMPLE", "P", LinkedPatientIds, PasswordHasher.Hash("medvault"))
            : null;

        public Task<HisAccount?> FindAccountAsync(string mobile, string type, CancellationToken ct) =>
            Task.FromResult(mobile == Mobile ? Account : null);

        public Task<IReadOnlyList<ResultItem>?> GetResultsAsync(HisAccount a, string r, CancellationToken ct) =>
            Task.FromResult(Results);

        public Task<IReadOnlyList<RequestItem>> GetRequestsAsync(HisAccount a, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RequestItem>>([]);

        public Task<IReadOnlyList<RadiologyStudyItem>> GetRadiologyStudiesAsync(HisAccount a, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RadiologyStudyItem>>([]);

        public Task<RadiologyReport?> GetRadiologyReportAsync(HisAccount a, string s, CancellationToken ct) =>
            Task.FromResult<RadiologyReport?>(null);

        public Task<bool> OwnsFileAsync(HisAccount a, string path, CancellationToken ct) =>
            Task.FromResult(false);
    }
}
