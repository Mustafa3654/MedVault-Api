using System.Text.Json;
using MedVault.Api.Contracts;

namespace MedVault.Api.Data;

/// <summary>
/// Serves the real MedVault captures from Data/Seed so the mobile app can be developed and the JSON
/// contract verified before the HIS schema is available. Not for production - see SqlHisRepository.
/// </summary>
public sealed class MockHisRepository : IHisRepository
{
    private const string SeedMobile = "00000000";
    private const string SeedRequestNb = "2026001001";
    private const string SeedStudyNb = "2604120017";

    private readonly IReadOnlyList<RequestItem> _requests;
    private readonly IReadOnlyList<ResultItem> _results;
    private readonly HisAccount _account;

    public MockHisRepository(IWebHostEnvironment env, IConfiguration config)
    {
        var seedDir = Path.Combine(env.ContentRootPath, "Data", "Seed");

        var requests = JsonSerializer.Deserialize<RequestsResponse>(
            File.ReadAllText(Path.Combine(seedDir, $"requests-{SeedMobile}.json")));
        // The captured seed predates `name`/`lab_name` on request rows; fill them the same way the
        // SQL repository does so both providers answer with one shape.
        var lab = config["Facility:DisplayName"];
        _requests = (requests?.Requests ?? [])
            .Select(r => r with
            {
                Name = r.Name ?? r.PatientName,
                LabName = r.LabName ?? (string.IsNullOrWhiteSpace(lab) ? null : lab),
            })
            .ToList();

        var results = JsonSerializer.Deserialize<ResultsResponse>(
            File.ReadAllText(Path.Combine(seedDir, $"results-{SeedRequestNb}.json")));
        _results = results?.Results ?? [];

        // Password for the seed patient, so the password-enabled login path can be exercised locally.
        // Configure with Auth:MockPassword; defaults to "medvault".
        var mockPassword = config["Auth:MockPassword"] ?? "medvault";

        _account = new HisAccount(
            Mobile: SeedMobile,
            Name: "ALEX EXAMPLE",
            Type: "P",
            LinkedPatientIds: ["ALEX  EXAMPLE", "CASEY  EXAMPLE", "TAYLOR ALEX EXAMPLE"],
            PasswordHash: PasswordHasher.Hash(mockPassword));
    }

    public Task<HisAccount?> FindAccountAsync(string mobile, string type, CancellationToken ct) =>
        Task.FromResult(mobile == SeedMobile && type == "P" ? _account : null);

    public Task<IReadOnlyList<RequestItem>> GetRequestsAsync(HisAccount account, CancellationToken ct) =>
        Task.FromResult(account.Mobile == SeedMobile ? _requests : []);

    public Task<IReadOnlyList<ResultItem>?> GetResultsAsync(HisAccount account, string requestNb, CancellationToken ct)
    {
        if (account.Mobile != SeedMobile || !_requests.Any(r => r.RequestNb == requestNb))
            return Task.FromResult<IReadOnlyList<ResultItem>?>(null);

        // Rule 1: unvalidated rows never reach the patient.
        var visible = requestNb == SeedRequestNb
            ? _results.Where(r => r.IsValid == "Y").ToList()
            : [];

        return Task.FromResult<IReadOnlyList<ResultItem>?>(visible);
    }

    public Task<IReadOnlyList<RadiologyStudyItem>> GetRadiologyStudiesAsync(HisAccount account, CancellationToken ct)
    {
        if (account.Mobile != SeedMobile)
            return Task.FromResult<IReadOnlyList<RadiologyStudyItem>>([]);

        // Illustrative only - no real radiology capture exists yet. Shape follows spec section 7.2.
        return Task.FromResult<IReadOnlyList<RadiologyStudyItem>>(
        [
            new RadiologyStudyItem
            {
                StudyNb = SeedStudyNb,
                OrderDatetime = "2026-04-12 09:15:00",
                Status = "V",
                Modality = "CT",
                StudyName = "CT Chest with contrast",
                PdfPath = $"ReportPDF/{SeedStudyNb}/a91f3b7c22e.pdf",
                ReportDatetime = "2026-04-12 13:40:00",
                PatientName = "ALEX  EXAMPLE",
            }
        ]);
    }

    public Task<RadiologyReport?> GetRadiologyReportAsync(HisAccount account, string studyNb, CancellationToken ct)
    {
        if (account.Mobile != SeedMobile || studyNb != SeedStudyNb)
            return Task.FromResult<RadiologyReport?>(null);

        return Task.FromResult<RadiologyReport?>(new RadiologyReport
        {
            Name = "ALEX EXAMPLE",
            Birthdate = "1990-01-01",
            StudyName = "CT Chest with contrast",
            Modality = "CT",
            StudyDatetime = "2026-04-12 09:15:00",
            ReportDatetime = "2026-04-12 13:40:00",
            Radiologist = "DR. SAM EXAMPLE",
            ClinicalHistory = "Persistent cough, 3 weeks",
            Technique = "Axial images obtained after IV contrast",
            Findings = "Sample findings text. Replace with HIS data.",
            Impression = "Sample impression text. Replace with HIS data.",
            IsValid = "Y",
            PdfPath = $"ReportPDF/{SeedStudyNb}/a91f3b7c22e.pdf",
        });
    }

    public async Task<bool> OwnsFileAsync(HisAccount account, string relativePath, CancellationToken ct)
    {
        var requests = await GetRequestsAsync(account, ct);
        if (requests.Any(r => PathsEqual(r.PdfPath, relativePath)))
            return true;

        var studies = await GetRadiologyStudiesAsync(account, ct);
        return studies.Any(s => PathsEqual(s.PdfPath, relativePath));
    }

    /// <summary>
    /// pdf_path arrives from the HIS with either separator and is echoed back by the client,
    /// so compare on a normalised form rather than raw string equality.
    /// </summary>
    private static bool PathsEqual(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
