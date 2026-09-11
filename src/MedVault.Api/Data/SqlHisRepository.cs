using System.Data.Common;
using MedVault.Api.Contracts;
using Microsoft.Data.SqlClient;

namespace MedVault.Api.Data;

/// <summary>
/// Reads from the HIS SQL database.
///
/// The queries themselves live in configuration (His:Sql:*) rather than in this file, so the HIS team
/// can supply them without a rebuild. Each query must return column names that match the JSON field
/// names in the spec - alias them if the underlying HIS columns are named differently, e.g.
///
///     SELECT o.ORDNUM AS request_nb, CONVERT(varchar(19), o.ORDDATE, 120) AS order_datetime, ...
///
/// Anything this class does not find in the result set comes back as null, which is a valid value for
/// most fields - so check the acceptance list in spec section 10 rather than assuming a silent gap.
///
/// The account used here needs SELECT only. It must never be able to write to the HIS.
/// </summary>
public sealed class SqlHisRepository(
    IConfiguration config,
    ILogger<SqlHisRepository> logger,
    ReferenceRanges ranges) : IHisRepository
{
    private readonly string _facility = config["Facility:DisplayName"] ?? "";

    private readonly string _connectionString =
        config.GetConnectionString("His")
        ?? throw new InvalidOperationException("ConnectionStrings:His is not configured.");

    private string Sql(string key) =>
        config[$"His:Sql:{key}"]
        ?? throw new InvalidOperationException(
            $"His:Sql:{key} is not configured. See docs/MedVault-HIS-API-Specification.md section 6.");

    // ---------- Login ----------

    public async Task<HisAccount?> FindAccountAsync(string mobile, string type, CancellationToken ct)
    {
        // Expected columns: name, password_hash (optional), patient_id
        // One row per linked patient (spec section 6.1) - the family group sharing this mobile number.
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql("FindAccount");
        cmd.Parameters.Add(new SqlParameter("@mobile", mobile));
        cmd.Parameters.Add(new SqlParameter("@type", type));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var row = new Row(reader);

        string? name = null, passwordHash = null;
        var patientIds = new List<string>();

        while (await reader.ReadAsync(ct))
        {
            name ??= row.Str("name");
            passwordHash ??= row.Str("password_hash");
            if (row.Str("patient_id") is { Length: > 0 } id) patientIds.Add(id);
        }

        if (name is null) return null;
        return new HisAccount(mobile, name, type, patientIds, passwordHash);
    }

    // ---------- Requests ----------

    public async Task<IReadOnlyList<RequestItem>> GetRequestsAsync(HisAccount account, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql("GetRequests");
        cmd.Parameters.Add(new SqlParameter("@mobile", account.Mobile));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var row = new Row(reader);
        var items = new List<RequestItem>();

        while (await reader.ReadAsync(ct))
        {
            var patientName = row.Str("patient_name") ?? "";

            items.Add(new RequestItem
            {
                RequestNb = row.Str("request_nb") ?? "",
                OrderDatetime = row.Str("order_datetime") ?? "",

                // A query that does not distinguish released from pending orders would make every
                // order look ready and fire a notification at order time, so default to pending.
                Status = row.Str("status") ?? "P",
                PdfPath = row.Str("pdf_path"),
                ReportDatetime = row.Str("report_datetime"),
                PatientName = patientName,

                // The HIS has one name column; `name` is the MedVault alias for it.
                Name = row.Str("name") ?? patientName,
                CatName = row.Str("cat_name"),
                BirthDate = row.Str("birth_date"),
                LabName = row.Str("lab_name") ?? NullIfEmpty(_facility),
            });
        }

        return items;
    }

    // ---------- Results ----------

    public async Task<IReadOnlyList<ResultItem>?> GetResultsAsync(HisAccount account, string requestNb, CancellationToken ct)
    {
        // Ownership is decided here, on the account's own order list, and not by whether the results
        // query happened to return rows. Most orders in the wild are still pending - four of the five
        // in the MedVault capture have no report_datetime - and an order with no validated rows yet must
        // read as "nothing to show" (an empty list), not as "no such order" (a 404).
        var requests = await GetRequestsAsync(account, ct);
        if (!requests.Any(r => string.Equals(r.RequestNb, requestNb, StringComparison.OrdinalIgnoreCase)))
            return null;

        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        // @mobile is passed so the query can join back to the owning patient. The query MUST filter on
        // it - do not rely on the request number alone, or any patient can read any other patient's
        // report by guessing a number (spec section 9, rule 4).
        cmd.CommandText = Sql("GetResults");
        cmd.Parameters.Add(new SqlParameter("@mobile", account.Mobile));
        cmd.Parameters.Add(new SqlParameter("@requestnb", requestNb));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var row = new Row(reader);
        var items = new List<ResultItem>();

        while (await reader.ReadAsync(ct))
        {
            var item = new ResultItem
            {
                Name = row.Str("name") ?? "",
                Birthdate = row.Str("birthdate"),
                Id = row.Int("id") ?? 0,
                TestName = row.Str("test_name") ?? "",
                IsTitle = row.Str("is_title"),
                GroupName = row.Str("group_name"),
                GroupInstruction = row.Str("group_instruction"),
                GroupClinicalRemark = row.Str("group_clinical_remark"),
                Unit = row.Str("unit"),
                RefRange = row.Str("ref_range"),
                TestType = row.Str("test_type"),
                TestId = row.Int("test_id"),
                IsValid = row.Str("is_valid"),
                CategoryNum = row.Int("category_num"),
                Result = row.Str("result"),
                ResultTxt = row.Str("result_txt"),
                Sign = row.Str("sign"),
                Position = row.Int("position"),
                IsMain = row.Str("is_main"),
                PrevResultVal = row.Str("prev_result_val"),
                PrevOrderId = row.Str("prev_order_id"),
                FieldNum = row.Str("field_num"),
                ClinicalRemark = row.Str("clinical_remark"),
                MethodInstruction = row.Str("method_instruction"),
                CodeRemark = row.Str("code_remark"),
                IsPrinted = row.Str("is_printed"),
                CalcResult = row.Str("calc_result"),
                CalcUnit = row.Str("calc_unit"),
                GroupPrinted = row.Str("group_printed"),
                NeedValidation = row.Str("need_validation"),
                HighlightTxt = row.Str("highlight_txt"),
                GroupOrder = row.Int("group_order"),
                GroupNum = row.Int("group_num"),
                CatNum = row.Int("cat_num"),
                CatName = row.Str("cat_name"),
                ResultStatus = row.Str("result_status"),
                DecPts = row.Int("dec_pts"),
            };

            // Rule 1: unvalidated rows never reach the patient. Enforced here as well as in SQL,
            // because a query change should not be able to leak preliminary results.
            if (item.IsValid != "Y") continue;

            // The Bekaa schema has no per-test group, only the category (HEMATOLOGY, CHEMISTRY...).
            // Without this every row falls into the app's single "Other" section; with it the report
            // is sectioned the same way the printed one is.
            if (string.IsNullOrWhiteSpace(item.GroupName) && item.CatName is { } cat)
                item = item with { GroupName = NullIfEmpty(cat.Trim()) };

            // ref_range and result_status come from the catalogue - neither exists in the HIS.
            items.Add(ranges.Annotate(item));
        }

        // Empty is a valid answer: the order is the account's, the results are not released yet.
        // "Not found or not yours" was already handled by the ownership check above.
        return items;
    }

    // ---------- Radiology ----------

    public async Task<IReadOnlyList<RadiologyStudyItem>> GetRadiologyStudiesAsync(HisAccount account, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql("GetRadiologyStudies");
        cmd.Parameters.Add(new SqlParameter("@mobile", account.Mobile));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var row = new Row(reader);
        var items = new List<RadiologyStudyItem>();

        while (await reader.ReadAsync(ct))
        {
            items.Add(new RadiologyStudyItem
            {
                StudyNb = row.Str("study_nb") ?? "",
                OrderDatetime = row.Str("order_datetime") ?? "",
                Status = row.Str("status") ?? "",
                Modality = row.Str("modality"),
                StudyName = row.Str("study_name"),
                PdfPath = row.Str("pdf_path"),
                ReportDatetime = row.Str("report_datetime"),
                PatientName = row.Str("patient_name") ?? "",
            });
        }

        return items;
    }

    public async Task<RadiologyReport?> GetRadiologyReportAsync(HisAccount account, string studyNb, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql("GetRadiologyReport");
        cmd.Parameters.Add(new SqlParameter("@mobile", account.Mobile));
        cmd.Parameters.Add(new SqlParameter("@studynb", studyNb));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var row = new Row(reader);

        if (!await reader.ReadAsync(ct)) return null;

        var report = new RadiologyReport
        {
            Name = row.Str("name") ?? "",
            Birthdate = row.Str("birthdate"),
            StudyName = row.Str("study_name"),
            Modality = row.Str("modality"),
            StudyDatetime = row.Str("study_datetime"),
            ReportDatetime = row.Str("report_datetime"),
            Radiologist = row.Str("radiologist"),
            ClinicalHistory = row.Str("clinical_history"),
            Technique = row.Str("technique"),
            Findings = row.Str("findings"),
            Impression = row.Str("impression"),
            IsValid = row.Str("is_valid"),
            PdfPath = row.Str("pdf_path"),
        };

        return report.IsValid == "Y" ? report : null;
    }

    // ---------- Files ----------

    public async Task<bool> OwnsFileAsync(HisAccount account, string relativePath, CancellationToken ct)
    {
        // Deliberately re-queries rather than trusting anything the client sent. The only paths a
        // patient may download are the ones their own orders and studies point at.
        var normalised = relativePath.Replace('\\', '/');

        var requests = await GetRequestsAsync(account, ct);
        if (requests.Any(r => Matches(r.PdfPath, normalised))) return true;

        var studies = await GetRadiologyStudiesAsync(account, ct);
        return studies.Any(s => Matches(s.PdfPath, normalised));

        static bool Matches(string? stored, string candidate) =>
            stored is not null &&
            string.Equals(stored.Replace('\\', '/'), candidate, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Plumbing ----------

    /// <summary>The app distinguishes null from "": a missing value must be null, not empty.</summary>
    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to open HIS connection");
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Column lookup by name, tolerant of columns the query does not return.
    /// Values are converted to the contract's type: every text field is emitted as a JSON string even
    /// when the HIS column is numeric, because the mobile app reads result/field_num/etc. as strings.
    /// </summary>
    private readonly struct Row
    {
        private readonly DbDataReader _reader;
        private readonly Dictionary<string, int> _ordinals;

        public Row(DbDataReader reader)
        {
            _reader = reader;
            _ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) _ordinals[reader.GetName(i)] = i;
        }

        private object? Raw(string name) =>
            _ordinals.TryGetValue(name, out var i) && !_reader.IsDBNull(i) ? _reader.GetValue(i) : null;

        public string? Str(string name) => Raw(name) switch
        {
            null => null,
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
            var v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
        };

        public int? Int(string name) => Raw(name) switch
        {
            null => null,
            int i => i,
            var v => int.TryParse(
                Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) ? parsed : null,
        };
    }
}
