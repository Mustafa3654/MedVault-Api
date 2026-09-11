using System.Text.RegularExpressions;
using MedVault.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MedVault.Api.Tests;

/// <summary>
/// Runs the real SqlHisRepository against the real HIS. Skipped unless MEDVAULT_HIS_CONNECTION is
/// set, so `dotnet test` on a laptop or in CI stays exactly as it is today.
///
/// Three rules govern everything in this file, because it touches live patient data:
///   1. Read only. Nothing here writes, and one of the tests proves the account cannot.
///   2. No PHI in output. Assertions report counts, shapes and field names - never a name, a birth
///      date or a result value. A failing test must be safe to paste into a ticket.
///   3. Consented test patient only. MEDVAULT_TEST_MOBILE must be a number the hospital has agreed
///      to use for testing, not a number picked out of the database.
///
/// See docs/MedVault-HIS-API-Specification.md section 10 for what acceptance means.
/// </summary>
public sealed class HisIntegrationTests
{
    // ---------- gating ----------

    private static string? Connection => Environment.GetEnvironmentVariable("MEDVAULT_HIS_CONNECTION");
    private static string? TestMobile => Environment.GetEnvironmentVariable("MEDVAULT_TEST_MOBILE");
    private static string? OtherMobile => Environment.GetEnvironmentVariable("MEDVAULT_OTHER_MOBILE");

    private sealed class HisFactAttribute : FactAttribute
    {
        public HisFactAttribute(bool needsPatient = true)
        {
            if (Connection is null)
                Skip = "Set MEDVAULT_HIS_CONNECTION to run the HIS integration tests.";
            else if (needsPatient && TestMobile is null)
                Skip = "Set MEDVAULT_TEST_MOBILE to a consented test patient's mobile number.";
        }
    }

    private sealed class HisTheoryAttribute : TheoryAttribute
    {
        public HisTheoryAttribute()
        {
            if (Connection is null)
                Skip = "Set MEDVAULT_HIS_CONNECTION to run the HIS integration tests.";
        }
    }

    // ---------- 1. the connection itself ----------

    /// <summary>
    /// The spec requires a SELECT-only account (section 9). This asks SQL Server what the account is
    /// allowed to do rather than trying a write to find out - never probe a permission boundary by
    /// crossing it on a hospital's production database.
    /// </summary>
    [HisFact(needsPatient: false)]
    public async Task The_his_account_can_read_and_cannot_write()
    {
        await using var conn = new SqlConnection(Connection);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT permission_name
            FROM sys.fn_my_permissions(NULL, 'DATABASE')
            WHERE permission_name IN ('SELECT', 'INSERT', 'UPDATE', 'DELETE', 'ALTER', 'CONTROL')
            """;

        var granted = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) granted.Add(reader.GetString(0).ToUpperInvariant());

        Assert.Contains("SELECT", granted);

        var writes = granted.Where(p => p != "SELECT").ToArray();
        Assert.True(writes.Length == 0,
            $"The HIS account has write permissions: {string.Join(", ", writes)}. " +
            "MedVault must connect with a SELECT-only login (spec section 9).");
    }

    // ---------- 2. the queries, without touching a single row ----------

    /// <summary>
    /// sp_describe_first_result_set validates a query and returns its column metadata WITHOUT running
    /// it, so this proves the SQL parses and that the aliases match the JSON field names before any
    /// patient data is read. It is the first thing to run when the HIS team hands over section 6.
    /// </summary>
    [HisTheory]
    [InlineData("FindAccount", "@mobile varchar(50), @type varchar(4)", "name", "patient_id")]
    [InlineData("GetRequests", "@mobile varchar(50)",
        "request_nb", "order_datetime", "status", "pdf_path", "report_datetime", "patient_name")]
    [InlineData("GetResults", "@mobile varchar(50), @requestnb varchar(50)",
        "name", "test_name", "is_valid", "result", "unit", "ref_range", "cat_name")]
    [InlineData("GetRadiologyStudies", "@mobile varchar(50)",
        "study_nb", "order_datetime", "status", "patient_name")]
    [InlineData("GetRadiologyReport", "@mobile varchar(50), @studynb varchar(50)",
        "name", "study_name", "is_valid", "findings", "impression")]
    public async Task Each_configured_query_parses_and_exposes_the_contract_columns(
        string key, string parameters, params string[] requiredColumns)
    {
        using var factory = new HisFactory();
        var sql = factory.Services.GetRequiredService<IConfiguration>()[$"His:Sql:{key}"];

        Assert.True(!string.IsNullOrWhiteSpace(sql) && !IsStillATodo(sql),
            $"His:Sql:{key} has not been supplied yet - see spec section 6. " +
            "This test is the acceptance gate for that query.");

        await using var conn = new SqlConnection(Connection);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "sys.sp_describe_first_result_set";
        cmd.CommandType = System.Data.CommandType.StoredProcedure;
        cmd.Parameters.Add(new SqlParameter("@tsql", sql));
        cmd.Parameters.Add(new SqlParameter("@params", parameters));

        var columns = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            var nameOrdinal = reader.GetOrdinal("name");
            while (await reader.ReadAsync())
                if (!reader.IsDBNull(nameOrdinal)) columns.Add(reader.GetString(nameOrdinal));
        }

        var missing = requiredColumns
            .Where(c => !columns.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(missing.Length == 0,
            $"His:Sql:{key} does not return: {string.Join(", ", missing)}. " +
            $"It returns: {string.Join(", ", columns)}. Alias the HIS columns to the JSON field names.");
    }

    // ---------- 3. the repository, against the test patient ----------

    [HisFact]
    public async Task The_test_patient_resolves_to_exactly_one_account()
    {
        var account = await Repository().FindAccountAsync(TestMobile!, "P", default);

        Assert.NotNull(account);
        Assert.False(string.IsNullOrWhiteSpace(account.Name), "The account resolved but has no name.");

        // Family grouping is the behaviour the MedVault capture shows; a count is safe to print, names are not.
        Assert.True(account.LinkedPatientIds.Count > 0,
            "No linked patient ids - GetRequests will return nothing. Check the join in His:Sql:FindAccount.");
    }

    /// <summary>
    /// Checks the shape the mobile app depends on, not the values. order_datetime in particular has
    /// to be "yyyy-MM-dd HH:mm:ss" - a HIS column returned as a raw datetime serialises differently
    /// and the app's date parsing fails silently.
    /// </summary>
    [HisFact]
    public async Task Requests_have_the_contract_shape()
    {
        var account = await RequireAccount();
        var requests = await Repository().GetRequestsAsync(account, default);

        Assert.True(requests.Count > 0,
            "The test patient has no orders. Pick a test patient who has at least one.");

        for (var i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            Assert.True(!string.IsNullOrWhiteSpace(r.RequestNb), $"requests[{i}].request_nb is empty.");
            Assert.True(!string.IsNullOrWhiteSpace(r.PatientName), $"requests[{i}].patient_name is empty.");
            Assert.True(Timestamp.IsMatch(r.OrderDatetime),
                $"requests[{i}].order_datetime is not yyyy-MM-dd HH:mm:ss.");

            if (r.ReportDatetime is not null)
                Assert.True(Timestamp.IsMatch(r.ReportDatetime),
                    $"requests[{i}].report_datetime is not yyyy-MM-dd HH:mm:ss.");
        }
    }

    /// <summary>
    /// Spec section 10: no row with is_valid != "Y" is ever returned. This is the single most
    /// important assertion in the file - a preliminary result shown to a patient is a clinical
    /// incident, not a bug.
    /// </summary>
    [HisFact]
    public async Task No_unvalidated_result_is_ever_returned()
    {
        var account = await RequireAccount();
        var repository = Repository();
        var requests = await repository.GetRequestsAsync(account, default);

        var checkedAny = false;
        foreach (var request in requests)
        {
            var results = await repository.GetResultsAsync(account, request.RequestNb, default);
            if (results is null) continue;

            checkedAny = true;
            Assert.DoesNotContain(results, r => r.IsValid != "Y");
        }

        Assert.True(checkedAny,
            "No order returned a result set, so the is_valid rule was never exercised. " +
            "Choose a test patient with at least one released report.");
    }

    /// <summary>
    /// The fix from this round, verified where it actually matters. An order the patient owns but
    /// whose results are not released yet must be an empty list, never a 404 - most orders are in
    /// that state.
    /// </summary>
    [HisFact]
    public async Task An_owned_order_never_reports_itself_as_missing()
    {
        var account = await RequireAccount();
        var repository = Repository();
        var requests = await repository.GetRequestsAsync(account, default);

        foreach (var request in requests)
        {
            var results = await repository.GetResultsAsync(account, request.RequestNb, default);

            Assert.True(results is not null,
                $"Order {request.RequestNb} is in the patient's own list but GetResults returned null, " +
                "which the endpoint turns into a 404. Owned-but-pending must be an empty list.");
        }
    }

    /// <summary>
    /// Spec section 10: another patient's order number returns nothing. Runs only when a second
    /// consented mobile number is supplied, because it needs a real order that really belongs to
    /// someone else - the check is worthless against a made-up number.
    /// </summary>
    [HisFact]
    public async Task One_patient_cannot_read_another_patients_order()
    {
        if (OtherMobile is null)
        {
            Assert.Fail("Set MEDVAULT_OTHER_MOBILE to a second consented test patient to run the " +
                        "cross-patient check (spec section 10). Do not skip this before go-live.");
        }

        var repository = Repository();

        var other = await repository.FindAccountAsync(OtherMobile!, "P", default);
        Assert.NotNull(other);

        var theirRequests = await repository.GetRequestsAsync(other, default);
        Assert.True(theirRequests.Count > 0, "The second test patient has no orders to attempt.");

        var mine = await RequireAccount();
        foreach (var theirs in theirRequests)
        {
            var leaked = await repository.GetResultsAsync(mine, theirs.RequestNb, default);

            Assert.True(leaked is null,
                $"Patient A read order {theirs.RequestNb}, which belongs to patient B. " +
                "His:Sql:GetResults must filter on @mobile as well as @requestnb (spec section 9, rule 4).");
        }
    }

    // ---------- helpers ----------

    private static readonly Regex Timestamp =
        new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", RegexOptions.Compiled);

    /// <summary>A query that is still a comment block of TODOs has not been supplied.</summary>
    private static bool IsStillATodo(string sql) =>
        sql.Split('\n').All(line => line.TrimStart().StartsWith("--") || line.TrimStart().Length == 0);

    private static async Task<HisAccount> RequireAccount()
    {
        var account = await Repository().FindAccountAsync(TestMobile!, "P", default);
        Assert.NotNull(account);
        return account;
    }

    /// <summary>
    /// Resolves the real SqlHisRepository out of the real application's container, so these tests
    /// exercise the shipping code path and the shipping configuration - not a copy of it.
    /// </summary>
    private static IHisRepository Repository()
    {
        var factory = new HisFactory();
        var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IHisRepository>();
    }

    private sealed class HisFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("His:Provider", "sql");
            builder.UseSetting("ConnectionStrings:His", Connection);
        }
    }
}
