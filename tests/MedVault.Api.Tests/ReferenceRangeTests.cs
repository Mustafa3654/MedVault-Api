using MedVault.Api.Contracts;
using MedVault.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedVault.Api.Tests;

/// <summary>
/// ref_range and result_status for the Bekaa HIS.
///
/// Neither exists in that database - ranges are stored nowhere, and TRLAB.ABNORMAL is NULL in all
/// 242,297 rows - so both come from a catalogue file. The rule these tests exist to hold is that a
/// test with no confirmed range returns null rather than "N": a hardcoded "N" told the patient a CRP
/// of 60 was normal, which is worse than showing no gauge at all.
/// </summary>
public sealed class ReferenceRangeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("medvault-ranges").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_numeric_result_inside_the_range_is_normal()
    {
        var annotated = Annotate("""{"tests":[{"test_name":"Iron","low":70,"high":180}]}""",
            Row("Iron", result: "120"));

        Assert.Equal("<div>70-180</div>", annotated.RefRange);
        Assert.Equal("N", annotated.ResultStatus);
    }

    [Theory]
    [InlineData("240", "H", "High")]
    [InlineData("40", "L", "Low")]
    public void A_result_outside_the_range_is_flagged_and_signed(string result, string status, string sign)
    {
        var annotated = Annotate("""{"tests":[{"test_name":"Iron","low":70,"high":180}]}""",
            Row("Iron", result: result));

        Assert.Equal(status, annotated.ResultStatus);
        Assert.Equal(sign, annotated.Sign);
    }

    /// <summary>The case that started this: a CRP of 60 against an upper bound of 5.</summary>
    [Fact]
    public void An_upper_bound_only_range_renders_and_flags()
    {
        var annotated = Annotate("""{"tests":[{"test_name":"CRP","high":5}]}""",
            Row("CRP", result: "60"));

        Assert.Equal("<div>&lt; 5</div>", annotated.RefRange);
        Assert.Equal("H", annotated.ResultStatus);
    }

    [Fact]
    public void A_test_with_no_catalogue_entry_stays_null_rather_than_normal()
    {
        var annotated = Annotate("""{"tests":[{"test_name":"Iron","low":70,"high":180}]}""",
            Row("Ferritin", result: "60"));

        Assert.Null(annotated.RefRange);
        Assert.Null(annotated.ResultStatus);
    }

    [Fact]
    public void A_missing_catalogue_leaves_every_row_untouched()
    {
        var annotated = Annotate(catalogue: null, Row("Iron", result: "120"));

        Assert.Null(annotated.RefRange);
        Assert.Null(annotated.ResultStatus);
    }

    /// <summary>
    /// A range that can only be written out, like "Negative", still gets displayed - there is just
    /// nothing to compare a value against, so no flag is invented.
    /// </summary>
    [Fact]
    public void A_text_only_range_is_shown_without_a_flag()
    {
        var annotated = Annotate("""{"tests":[{"test_name":"HCV AB","text":"Negative"}]}""",
            Row("HCV AB", result: "NEGATIVE"));

        Assert.Equal("<div>Negative</div>", annotated.RefRange);
        Assert.Null(annotated.ResultStatus);
    }

    [Fact]
    public void A_non_numeric_result_gets_the_range_but_no_flag()
    {
        var annotated = Annotate("""{"tests":[{"test_name":"Glucose","low":70,"high":110}]}""",
            Row("Glucose", result: "HEMOLYSED"));

        Assert.Equal("<div>70-110</div>", annotated.RefRange);
        Assert.Null(annotated.ResultStatus);
    }

    /// <summary>Results below the analyser's detection limit are reported as "&lt; 0.5", not as a number.</summary>
    [Theory]
    [InlineData("<0.5", "L")]
    [InlineData("> 400", "H")]
    public void An_open_ended_result_is_flagged_from_its_bound(string result, string expected)
    {
        var annotated = Annotate("""{"tests":[{"test_name":"Ferritin","low":15,"high":300}]}""",
            Row("Ferritin", result: result));

        Assert.Equal(expected, annotated.ResultStatus);
    }

    /// <summary>Codes are the stable key; a re-typed test name must not shadow a matching code.</summary>
    [Fact]
    public void The_test_code_wins_over_the_test_name()
    {
        var annotated = Annotate(
            """{"tests":[{"code":"0101","low":12,"high":16},{"test_name":"HGB","low":1,"high":2}]}""",
            Row("HGB", result: "14", fieldNum: "0101"));

        Assert.Equal("<div>12-16</div>", annotated.RefRange);
        Assert.Equal("N", annotated.ResultStatus);
    }

    /// <summary>
    /// If the HIS ever starts supplying a real range or a real analyser flag, that is better than
    /// anything computed here and must not be overwritten.
    /// </summary>
    [Fact]
    public void Values_the_his_supplied_are_left_alone()
    {
        var annotated = Annotate("""{"tests":[{"test_name":"Iron","low":70,"high":180}]}""",
            Row("Iron", result: "240") with { RefRange = "<div>from the HIS</div>", ResultStatus = "N" });

        Assert.Equal("<div>from the HIS</div>", annotated.RefRange);
        Assert.Equal("N", annotated.ResultStatus);
    }

    /// <summary>A catalogue that will not parse must not take the API down mid-clinic.</summary>
    [Fact]
    public void A_malformed_catalogue_degrades_to_no_ranges()
    {
        var annotated = Annotate("{ this is not json", Row("Iron", result: "120"));

        Assert.Null(annotated.RefRange);
        Assert.Null(annotated.ResultStatus);
    }

    // ---------- helpers ----------

    private ResultItem Annotate(string? catalogue, ResultItem row)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        if (catalogue is not null) File.WriteAllText(path, catalogue);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["His:ReferenceRangeCatalogue"] = path })
            .Build();

        var ranges = new ReferenceRanges(new StubEnvironment(_dir), config, NullLogger<ReferenceRanges>.Instance);
        return ranges.Annotate(row);
    }

    private static ResultItem Row(string testName, string result, string? fieldNum = null) => new()
    {
        Name = "ALEX  EXAMPLE",
        Id = 1,
        TestName = testName,
        Result = result,
        FieldNum = fieldNum,
        IsValid = "Y",
        Sign = "",
    };

    private sealed class StubEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MedVault.Api.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
        public string EnvironmentName { get; set; } = "Test";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new PhysicalFileProvider(root);
    }
}
