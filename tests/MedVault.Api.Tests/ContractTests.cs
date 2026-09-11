using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MedVault.Api.Files;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MedVault.Api.Tests;

/// <summary>
/// The point of these tests is fidelity, not coverage: the mobile app already exists and expects the
/// MedVault payload exactly, so the responses are compared against the real capture rather than against
/// hand-written expectations.
/// </summary>
public sealed class ContractTests : IClassFixture<ApiFactory>
{
    private const string Mobile = "00000000";
    private const string Password = "medvault";
    private const string RequestNb = "2026001001";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ContractTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<string> LoginAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/mobile/login",
            new { username = Mobile, password = Password, type = "P" });

        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return json["token"]!.GetValue<string>();
    }

    // ---------- API 1 ----------

    [Fact]
    public async Task Login_returns_the_vmc_field_set_including_capital_Lab()
    {
        var response = await _client.PostAsJsonAsync("/api/mobile/login",
            new { username = Mobile, password = Password, type = "P" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        Assert.Equal(["success", "token", "user", "name", "Lab"], json.Select(p => p.Key));
        Assert.True(json["success"]!.GetValue<bool>());
        Assert.Equal(Mobile, json["user"]!.GetValue<string>());
        Assert.Equal("ALEX EXAMPLE", json["name"]!.GetValue<string>());
        Assert.Equal("MedVault Demo Hospital", json["Lab"]!.GetValue<string>());

        // MedVault issues 80 hex characters; the app stores it as an opaque string but the length is a
        // cheap check that we did not accidentally shorten it.
        Assert.Equal(80, json["token"]!.GetValue<string>().Length);
    }

    [Theory]
    [InlineData(Mobile, "wrong-password")]
    [InlineData("70999999", Password)]
    public async Task Login_failures_are_indistinguishable(string username, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/mobile/login",
            new { username, password, type = "P" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        Assert.False(json["success"]!.GetValue<bool>());
        Assert.Equal("Invalid credentials", json["message"]!.GetValue<string>());
    }

    // ---------- API 2 ----------

    [Fact]
    public async Task Requests_list_matches_the_capture_including_family_members()
    {
        var token = await LoginAsync();
        var body = await _client.GetStringAsync($"/api/mobile/request?tcToken={token}");

        var expected = JsonNode.Parse(await File.ReadAllTextAsync(ApiFactory.SeedFile("requests-00000000.json")))!;

        // pdf_paths is ours, not MedVault's - see RequestItem. The capture is the untouched record of what
        // the hospital's API returns, so it is not edited to match us; the additive key is removed
        // here instead and asserted separately below. Every MedVault field stays strictly compared, so a
        // future change that drops or renames one still fails.
        var actual = JsonNode.Parse(body)!;
        foreach (var request in actual["requests"]!.AsArray())
            request!.AsObject().Remove("pdf_paths");

        AssertJson.DeepEqual(expected, actual);

        var requests = JsonNode.Parse(body)!["requests"]!.AsArray();

        // Always present, never null, so the app can iterate without a null check. Empty here because
        // the mock provider's Files:Root holds no file named for these orders.
        foreach (var request in requests)
            Assert.NotNull(request!["pdf_paths"]?.AsArray());
        var names = requests.Select(r => r!["patient_name"]!.GetValue<string>()).Distinct();
        Assert.Equal(3, names.Count());   // one mobile number, three patients (spec section 3, item 2)

        // null must survive as null, not become "".
        Assert.Null(requests[1]!["pdf_path"]);
    }

    /// <summary>
    /// One visit produces several reports on the Bekaa server - 26L007599_101.PDF and _107 alongside
    /// each other, _100 the usual combined one - and the app needs all of them, not one arbitrary
    /// pick. Written with the real naming: uppercase .PDF, which the glob must still match on Windows.
    /// </summary>
    [Fact]
    public void All_reports_for_one_visit_are_returned_100_first()
    {
        var root = Path.Combine(Path.GetTempPath(), "medvault-multi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Deliberately created newest-first-is-wrong: _107 is written last, but _100 must lead.
            File.WriteAllText(Path.Combine(root, "26L007599_107.PDF"), "%PDF-1.4 urine");
            File.WriteAllText(Path.Combine(root, "26L007599_101.PDF"), "%PDF-1.4 haematology");
            File.WriteAllText(Path.Combine(root, "26L007599_100.PDF"), "%PDF-1.4 combined");

            // A different visit that must not be picked up, and an empty file that must be skipped.
            File.WriteAllText(Path.Combine(root, "26L0075990_100.PDF"), "%PDF-1.4 other visit");
            File.WriteAllText(Path.Combine(root, "26L007600_100.PDF"), "");

            var resolver = new PdfFileResolver(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["Files:Root"] = root })
                    .Build());

            Assert.Equal(
                ["ResultPDF/26L007599_100.PDF", "ResultPDF/26L007599_101.PDF", "ResultPDF/26L007599_107.PDF"],
                resolver.PathsForRequest("26L007599"));

            // pdf_path stays a single string for the existing app, and leads with the combined report.
            Assert.Equal("ResultPDF/26L007599_100.PDF", resolver.PathForRequest("26L007599"));

            // A zero-length file is a report still being written: not ready, not listed.
            Assert.Empty(resolver.PathsForRequest("26L007600"));

            // No file at all - the normal state for a pending order, and for the older visits whose
            // reports predate this folder.
            Assert.Empty(resolver.PathsForRequest("21L010867"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------- API 3 ----------

    [Fact]
    public async Task Results_match_the_vmc_capture_field_for_field()
    {
        var token = await LoginAsync();
        var body = await _client.GetStringAsync($"/api/mobile/results?requestnb={RequestNb}&tcToken={token}");

        var expected = JsonNode.Parse(await File.ReadAllTextAsync(ApiFactory.SeedFile($"results-{RequestNb}.json")))!;
        AssertJson.DeepEqual(expected, JsonNode.Parse(body)!);
    }

    [Fact]
    public async Task Results_preserve_html_and_entities_untouched()
    {
        var token = await LoginAsync();
        var body = await _client.GetStringAsync($"/api/mobile/results?requestnb={RequestNb}&tcToken={token}");
        var results = JsonNode.Parse(body)!["results"]!.AsArray();

        var iron = results.Single(r => r!["test_name"]!.GetValue<string>() == "Iron")!;
        Assert.Equal("&micro;g/dL", iron["unit"]!.GetValue<string>());
        Assert.Equal("<div>70-180</div>", iron["ref_range"]!.GetValue<string>());

        // Trailing space in cat_name is real data and the app groups on it.
        var esr = results.Single(r => r!["test_name"]!.GetValue<string>() == "ESR")!;
        Assert.Equal("Hematology ", esr["cat_name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Results_for_an_unknown_request_are_404_not_empty()
    {
        var token = await LoginAsync();
        var response = await _client.GetAsync($"/api/mobile/results?requestnb=9999999999&tcToken={token}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The mirror of the test above, and the reason both exist: "not yours" and "not ready yet" are
    /// different answers. Four of the five orders in the MedVault capture are still pending, so a 404 here
    /// would be the common case, not the edge case. Both repository implementations must agree.
    /// </summary>
    [Fact]
    public async Task Results_for_an_owned_but_pending_request_are_an_empty_list_not_404()
    {
        var token = await LoginAsync();
        var response = await _client.GetAsync($"/api/mobile/results?requestnb=2026001002&tcToken={token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal("2026001002", json["request_nb"]!.GetValue<string>());
        Assert.Empty(json["results"]!.AsArray());
    }

    // ---------- Error contract ----------

    /// <summary>
    /// A body that is not valid JSON must not be reported as 503. The app treats 503 as "the hospital
    /// is down" and would show an outage message for what is its own malformed request.
    /// </summary>
    [Fact]
    public async Task A_malformed_body_is_400_with_the_error_contract_not_503()
    {
        var response = await _client.PostAsync("/api/mobile/login",
            new StringContent("{", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        Assert.False(json["success"]!.GetValue<bool>());
        Assert.Equal("Bad request", json["message"]!.GetValue<string>());
    }

    /// <summary>
    /// The same guarantee, hosted as Production - which is what the hospital server runs.
    ///
    /// RouteHandlerOptions.ThrowOnBadRequest defaults to true in Development and false everywhere
    /// else, and WebApplicationFactory hosts as Development. So the test above passed while the
    /// deployed server answered every malformed body with a bare 400 and no content. Program.cs now
    /// forces the setting on; this test fails if that is ever removed.
    ///
    /// The three bodies are the ones seen in the field: a truncated body, the mobile number sent as a
    /// JSON number, and the same with a leading zero (which is not valid JSON at all).
    /// </summary>
    [Theory]
    [InlineData("{")]
    [InlineData("{\"username\":3191176,\"type\":\"P\"}")]
    [InlineData("{\"username\":03191176,\"type\":\"P\"}")]
    public async Task A_malformed_body_keeps_the_error_contract_in_production(string body)
    {
        using var factory = new ProductionApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/mobile/login",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync();
        Assert.NotEmpty(payload);

        var json = JsonNode.Parse(payload)!.AsObject();
        Assert.False(json["success"]!.GetValue<bool>());
        Assert.Equal("Bad request", json["message"]!.GetValue<string>());
    }

    // ---------- Auth ----------

    [Theory]
    [InlineData("/api/mobile/request")]
    [InlineData("/api/mobile/results?requestnb=2026001001")]
    [InlineData("/api/mobile/radiology")]
    [InlineData("/api/mobile/file?path=ResultPDF/2026001001/example-report.pdf")]
    public async Task Endpoints_reject_a_missing_or_bogus_token(string path)
    {
        var separator = path.Contains('?') ? '&' : '?';

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.GetAsync($"{path}{separator}tcToken=not-a-real-token")).StatusCode);
    }

    [Fact]
    public async Task Bearer_header_is_accepted_as_an_alternative_to_the_query_string()
    {
        var token = await LoginAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/mobile/request");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Logout_invalidates_the_token()
    {
        var token = await LoginAsync();
        (await _client.PostAsync($"/api/mobile/logout?tcToken={token}", null)).EnsureSuccessStatusCode();

        var response = await _client.GetAsync($"/api/mobile/request?tcToken={token}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- API 6 ----------

    [Fact]
    public async Task File_download_returns_the_pdf_for_an_owned_path()
    {
        var token = await LoginAsync();
        var response = await _client.GetAsync(
            $"/api/mobile/file?path=ResultPDF/2026001001/example-report.pdf&tcToken={token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("../../../Windows/win.ini")]
    [InlineData("ResultPDF/../../../Windows/win.ini")]
    [InlineData("/Windows/win.ini")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("ResultPDF/2026001001/../../secret.pdf")]
    public async Task File_download_refuses_to_escape_the_root(string path)
    {
        var token = await LoginAsync();
        var response = await _client.GetAsync(
            $"/api/mobile/file?path={Uri.EscapeDataString(path)}&tcToken={token}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task File_download_refuses_a_real_file_the_patient_does_not_own()
    {
        var token = await LoginAsync();
        var response = await _client.GetAsync(
            $"/api/mobile/file?path=ResultPDF/9999999999/someone-else.pdf&tcToken={token}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>
/// ApiFactory hosted as Production, for the handful of behaviours that differ by environment.
/// Everything else should use ApiFactory, which is shared across a whole test class.
/// </summary>
public sealed class ProductionApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("His:Provider", "mock");
        builder.UseSetting("Auth:LoginAttemptsPerWindow", "1000");
    }
}

public sealed class ApiFactory : WebApplicationFactory<Program>, IDisposable
{
    public string FilesRoot { get; } =
        Path.Combine(Path.GetTempPath(), "medvault-tests", Guid.NewGuid().ToString("N"));

    public ApiFactory()
    {
        // A file the seed patient owns, and one they do not - both must really exist on disk so the
        // traversal and ownership tests are not passing merely because nothing is there.
        WritePdf(Path.Combine(FilesRoot, "ResultPDF", "2026001001", "example-report.pdf"));
        WritePdf(Path.Combine(FilesRoot, "ResultPDF", "9999999999", "someone-else.pdf"));
        WritePdf(Path.Combine(FilesRoot, "secret.pdf"));
    }

    private static void WritePdf(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "%PDF-1.4 test");
    }

    public static string SeedFile(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Data", "Seed", name);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("His:Provider", "mock");
        builder.UseSetting("Auth:RequirePassword", "true");
        builder.UseSetting("Auth:MockPassword", "medvault");
        builder.UseSetting("Files:Root", FilesRoot);

        // The suite logs in far more often than a real client would; the limiter itself is covered
        // by RateLimitTests, which uses its own host.
        builder.UseSetting("Auth:LoginAttemptsPerWindow", "1000");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(FilesRoot))
            Directory.Delete(FilesRoot, recursive: true);
    }
}

internal static class AssertJson
{
    /// <summary>
    /// Order-sensitive for arrays, order-insensitive for object keys, and strict about null vs "".
    /// </summary>
    public static void DeepEqual(JsonNode expected, JsonNode actual, string path = "$")
    {
        switch (expected)
        {
            case JsonObject expectedObj:
                var actualObj = Assert.IsType<JsonObject>(actual);
                Assert.Equal(
                    expectedObj.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal),
                    actualObj.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));

                foreach (var (key, expectedValue) in expectedObj)
                {
                    var actualValue = actualObj[key];
                    if (expectedValue is null)
                    {
                        Assert.True(actualValue is null, $"{path}.{key}: expected null, got {actualValue?.ToJsonString()}");
                        continue;
                    }
                    Assert.True(actualValue is not null, $"{path}.{key}: expected a value, got null");
                    DeepEqual(expectedValue, actualValue!, $"{path}.{key}");
                }
                break;

            case JsonArray expectedArr:
                var actualArr = Assert.IsType<JsonArray>(actual);
                Assert.True(expectedArr.Count == actualArr.Count,
                    $"{path}: expected {expectedArr.Count} items, got {actualArr.Count}");

                for (var i = 0; i < expectedArr.Count; i++)
                    DeepEqual(expectedArr[i]!, actualArr[i]!, $"{path}[{i}]");
                break;

            default:
                Assert.True(
                    expected.ToJsonString() == actual.ToJsonString(),
                    $"{path}: expected {expected.ToJsonString()}, got {actual.ToJsonString()}");
                break;
        }
    }
}
