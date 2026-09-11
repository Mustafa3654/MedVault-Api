using System.Text.Json.Serialization;

namespace MedVault.Api.Contracts;

// The JSON shapes below are fixed by the existing MedVault contract and the MedVault mobile app.
// Property names, casing (note `Lab`), and null-vs-empty-string are all load bearing.
// See docs/MedVault-HIS-API-Specification.md sections 3-5.

// ---------- API 1: Login ----------

public sealed record LoginRequest
{
    [JsonPropertyName("username")] public string? Username { get; init; }
    [JsonPropertyName("password")] public string? Password { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
}

public sealed record LoginResponse
{
    [JsonPropertyName("success")] public required bool Success { get; init; }
    [JsonPropertyName("token")] public required string Token { get; init; }
    [JsonPropertyName("user")] public required string User { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>Facility display name. Capital "L" is intentional - the app reads `Lab`.</summary>
    [JsonPropertyName("Lab")] public required string Lab { get; init; }
}

public sealed record ErrorResponse
{
    [JsonPropertyName("success")] public bool Success => false;
    [JsonPropertyName("message")] public required string Message { get; init; }
}

// ---------- API 2: Requests list ----------

public sealed record RequestsResponse
{
    [JsonPropertyName("requests")] public required IReadOnlyList<RequestItem> Requests { get; init; }
}

public sealed record RequestItem
{
    [JsonPropertyName("request_nb")] public required string RequestNb { get; init; }
    [JsonPropertyName("order_datetime")] public required string OrderDatetime { get; init; }

    /// <summary>
    /// "V" once at least one result row has been released, "P" while the order is still pending.
    /// The app keys result-ready notifications off the P -> V transition, so this must never be
    /// hardcoded to "V" - an order that reads V from the moment it is created notifies the patient
    /// before the sample has been run.
    /// </summary>
    [JsonPropertyName("status")] public required string Status { get; init; }

    [JsonPropertyName("pdf_path")] public string? PdfPath { get; init; }

    /// <summary>
    /// Every report on disk for this order, not just the one <see cref="PdfPath"/> carries. One visit
    /// routinely produces several - 26L007599_101.PDF and 26L007599_107.PDF - which appear to be the
    /// laboratory's category sections, with _100 the usual combined report. Ordered by filename, so
    /// _100 is first and equals PdfPath.
    ///
    /// Additive: not part of the MedVault contract. Always present, empty when nothing is on disk yet, so
    /// the app can switch to it without a null check. PdfPath stays for consumers that read only it.
    /// </summary>
    [JsonPropertyName("pdf_paths")] public IReadOnlyList<string> PdfPaths { get; init; } = [];

    /// <summary>Release time. Null while pending, and null for historic orders released before the
    /// HIS began stamping one - so it confirms a release but its absence does not deny one.</summary>
    [JsonPropertyName("report_datetime")] public string? ReportDatetime { get; init; }

    [JsonPropertyName("patient_name")] public required string PatientName { get; init; }

    /// <summary>Same value as <see cref="PatientName"/>. Present because the MedVault contract carries
    /// `name` on request rows and other consumers read that key.</summary>
    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("cat_name")] public string? CatName { get; init; }
    [JsonPropertyName("birth_date")] public string? BirthDate { get; init; }

    /// <summary>Facility display name, falling back to Facility:DisplayName when the HIS has none.</summary>
    [JsonPropertyName("lab_name")] public string? LabName { get; init; }
}

// ---------- API 3: Results ----------

public sealed record ResultsResponse
{
    [JsonPropertyName("request_nb")] public required string RequestNb { get; init; }
    [JsonPropertyName("results")] public required IReadOnlyList<ResultItem> Results { get; init; }
}

/// <summary>
/// One row of a lab report. Every field must be present in the response even when null or "",
/// and null must not be normalised to "" - the app distinguishes them.
/// Declaration order matches the MedVault capture.
/// </summary>
public sealed record ResultItem
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("birthdate")] public string? Birthdate { get; init; }
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("test_name")] public required string TestName { get; init; }
    [JsonPropertyName("is_title")] public string? IsTitle { get; init; }
    [JsonPropertyName("group_name")] public string? GroupName { get; init; }
    [JsonPropertyName("group_instruction")] public string? GroupInstruction { get; init; }
    [JsonPropertyName("group_clinical_remark")] public string? GroupClinicalRemark { get; init; }

    /// <summary>May contain HTML entities, e.g. "&amp;micro;g/dL". Passed through verbatim.</summary>
    [JsonPropertyName("unit")] public string? Unit { get; init; }

    /// <summary>May contain HTML and embedded newlines, e.g. "&lt;div&gt;70-180&lt;/div&gt;". Passed through verbatim.</summary>
    [JsonPropertyName("ref_range")] public string? RefRange { get; init; }

    [JsonPropertyName("test_type")] public string? TestType { get; init; }
    [JsonPropertyName("test_id")] public int? TestId { get; init; }
    [JsonPropertyName("is_valid")] public string? IsValid { get; init; }
    [JsonPropertyName("category_num")] public int? CategoryNum { get; init; }
    [JsonPropertyName("result")] public string? Result { get; init; }
    [JsonPropertyName("result_txt")] public string? ResultTxt { get; init; }

    /// <summary>Abnormal flag. Observed values: "", "High", "Low". Display string only - colour from ResultStatus.</summary>
    [JsonPropertyName("sign")] public string? Sign { get; init; }

    [JsonPropertyName("position")] public int? Position { get; init; }
    [JsonPropertyName("is_main")] public string? IsMain { get; init; }
    [JsonPropertyName("prev_result_val")] public string? PrevResultVal { get; init; }
    [JsonPropertyName("prev_order_id")] public string? PrevOrderId { get; init; }
    [JsonPropertyName("field_num")] public string? FieldNum { get; init; }
    [JsonPropertyName("clinical_remark")] public string? ClinicalRemark { get; init; }
    [JsonPropertyName("method_instruction")] public string? MethodInstruction { get; init; }
    [JsonPropertyName("code_remark")] public string? CodeRemark { get; init; }
    [JsonPropertyName("is_printed")] public string? IsPrinted { get; init; }
    [JsonPropertyName("calc_result")] public string? CalcResult { get; init; }
    [JsonPropertyName("calc_unit")] public string? CalcUnit { get; init; }
    [JsonPropertyName("group_printed")] public string? GroupPrinted { get; init; }
    [JsonPropertyName("need_validation")] public string? NeedValidation { get; init; }
    [JsonPropertyName("highlight_txt")] public string? HighlightTxt { get; init; }
    [JsonPropertyName("group_order")] public int? GroupOrder { get; init; }
    [JsonPropertyName("group_num")] public int? GroupNum { get; init; }
    [JsonPropertyName("cat_num")] public int? CatNum { get; init; }

    /// <summary>Category tab in the app. Trailing spaces exist in real data ("Hematology ") - do not trim.</summary>
    [JsonPropertyName("cat_name")] public string? CatName { get; init; }

    /// <summary>Observed values: "N", "H", "L", "U", null.</summary>
    [JsonPropertyName("result_status")] public string? ResultStatus { get; init; }

    [JsonPropertyName("dec_pts")] public int? DecPts { get; init; }
}

// ---------- API 4/5: Radiology (new - see spec section 7) ----------

public sealed record RadiologyListResponse
{
    [JsonPropertyName("studies")] public required IReadOnlyList<RadiologyStudyItem> Studies { get; init; }
}

public sealed record RadiologyStudyItem
{
    [JsonPropertyName("study_nb")] public required string StudyNb { get; init; }
    [JsonPropertyName("order_datetime")] public required string OrderDatetime { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("modality")] public string? Modality { get; init; }
    [JsonPropertyName("study_name")] public string? StudyName { get; init; }
    [JsonPropertyName("pdf_path")] public string? PdfPath { get; init; }
    [JsonPropertyName("report_datetime")] public string? ReportDatetime { get; init; }
    [JsonPropertyName("patient_name")] public required string PatientName { get; init; }
}

public sealed record RadiologyReportResponse
{
    [JsonPropertyName("study_nb")] public required string StudyNb { get; init; }
    [JsonPropertyName("report")] public required RadiologyReport Report { get; init; }
}

public sealed record RadiologyReport
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("birthdate")] public string? Birthdate { get; init; }
    [JsonPropertyName("study_name")] public string? StudyName { get; init; }
    [JsonPropertyName("modality")] public string? Modality { get; init; }
    [JsonPropertyName("study_datetime")] public string? StudyDatetime { get; init; }
    [JsonPropertyName("report_datetime")] public string? ReportDatetime { get; init; }
    [JsonPropertyName("radiologist")] public string? Radiologist { get; init; }
    [JsonPropertyName("clinical_history")] public string? ClinicalHistory { get; init; }
    [JsonPropertyName("technique")] public string? Technique { get; init; }

    /// <summary>When the HIS stores one free-text blob, the whole report goes here and the
    /// other two stay "". Do not try to split it server side.</summary>
    [JsonPropertyName("findings")] public string? Findings { get; init; }

    [JsonPropertyName("impression")] public string? Impression { get; init; }
    [JsonPropertyName("is_valid")] public string? IsValid { get; init; }
    [JsonPropertyName("pdf_path")] public string? PdfPath { get; init; }
}
