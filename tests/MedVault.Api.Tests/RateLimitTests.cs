using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MedVault.Api.Tests;

/// <summary>
/// Login throttling exists to stop someone walking the mobile-number space to find out which numbers
/// belong to patients (spec section 9, rule 5). Uses its own host so the low limit does not affect
/// the contract tests.
/// </summary>
public sealed class RateLimitTests
{
    private sealed class ThrottledFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("His:Provider", "mock");
            builder.UseSetting("Auth:LoginAttemptsPerWindow", "3");
            builder.UseSetting("Auth:LoginWindowMinutes", "15");
        }
    }

    [Fact]
    public async Task Login_is_throttled_after_the_configured_number_of_attempts()
    {
        using var factory = new ThrottledFactory();
        var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/mobile/login",
                new { username = $"7010000{i}", password = "guess", type = "P" });
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK,
             HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests],
            statuses);
    }

    /// <summary>
    /// A bare 429 with no body would be the only error in this API that is not an ErrorResponse, and
    /// the app parses the body on every other path.
    /// </summary>
    [Fact]
    public async Task A_throttled_login_still_returns_the_error_contract()
    {
        using var factory = new ThrottledFactory();
        var client = factory.CreateClient();

        HttpResponseMessage response;
        do
        {
            response = await client.PostAsJsonAsync("/api/mobile/login",
                new { username = "00000000", password = "guess", type = "P" });
        }
        while (response.StatusCode != HttpStatusCode.TooManyRequests);

        var json = System.Text.Json.Nodes.JsonNode.Parse(
            await response.Content.ReadAsStringAsync())!.AsObject();

        Assert.False(json["success"]!.GetValue<bool>());
        Assert.Equal("Too many attempts", json["message"]!.GetValue<string>());
    }
}
