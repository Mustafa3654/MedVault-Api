using MedVault.Api.Contracts;

namespace MedVault.Api.Data;

/// <summary>
/// A patient (or doctor) account resolved from a mobile number.
/// <paramref name="LinkedPatientIds"/> is the family group: the MedVault contract returns orders for every
/// patient linked to the same mobile number. See spec section 3, open item 2.
/// </summary>
public sealed record HisAccount(
    string Mobile,
    string Name,
    string Type,
    IReadOnlyList<string> LinkedPatientIds,
    string? PasswordHash);

/// <summary>
/// Everything the API needs from the HIS. This is the only seam that changes when the real
/// FoxPro/SQL schema is known - see docs/MedVault-HIS-API-Specification.md section 6.
///
/// Implementations must enforce two rules:
///   1. Never return a row whose is_valid is not "Y" (unvalidated results must not reach patients).
///   2. Never return data for a request/study that does not belong to the supplied account.
///      Returning null for "not found" and "not yours" alike keeps the two indistinguishable.
/// </summary>
public interface IHisRepository
{
    Task<HisAccount?> FindAccountAsync(string mobile, string type, CancellationToken ct);

    Task<IReadOnlyList<RequestItem>> GetRequestsAsync(HisAccount account, CancellationToken ct);

    /// <summary>
    /// Null when the request does not exist or does not belong to <paramref name="account"/>.
    /// An empty list is different and must not be collapsed into null: it means the order is the
    /// account's but no validated results have been released yet, which is the normal state of most
    /// orders. Null becomes a 404, empty becomes 200 with "results": [].
    /// </summary>
    Task<IReadOnlyList<ResultItem>?> GetResultsAsync(HisAccount account, string requestNb, CancellationToken ct);

    Task<IReadOnlyList<RadiologyStudyItem>> GetRadiologyStudiesAsync(HisAccount account, CancellationToken ct);

    /// <summary>Null when the study does not exist or does not belong to <paramref name="account"/>.</summary>
    Task<RadiologyReport?> GetRadiologyReportAsync(HisAccount account, string studyNb, CancellationToken ct);

    /// <summary>
    /// True when <paramref name="relativePath"/> is a pdf_path this account is entitled to download.
    /// Path knowledge alone must never grant access.
    /// </summary>
    Task<bool> OwnsFileAsync(HisAccount account, string relativePath, CancellationToken ct);
}
