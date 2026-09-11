using MedVault.Api.Auth;
using MedVault.Api.Contracts;
using MedVault.Api.Data;
using MedVault.Api.Files;
using Microsoft.AspNetCore.Mvc;

namespace MedVault.Api.Endpoints;

public static class MobileEndpoints
{
    public static void MapMobileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mobile");

        group.MapPost("/login", LoginAsync).RequireRateLimiting("login");
        group.MapPost("/logout", Logout);
        group.MapGet("/request", GetRequestsAsync);
        group.MapGet("/results", GetResultsAsync);
        group.MapGet("/radiology", GetRadiologyAsync);
        group.MapGet("/radiologyreport", GetRadiologyReportAsync);
        group.MapGet("/file", GetFileAsync);
    }

    // ---------- API 1 ----------

    private static async Task<IResult> LoginAsync(
        [FromBody] LoginRequest body,
        IHisRepository his,
        ITokenService tokens,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var username = body.Username?.Trim();
        var type = string.IsNullOrWhiteSpace(body.Type) ? "P" : body.Type.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(username) || (type != "P" && type != "D"))
            return BadRequest();

        var account = await his.FindAccountAsync(username, type, ct);

        // Same message, same status and same amount of work for "no such number" and "wrong password",
        // so the endpoint cannot be used to discover which mobile numbers belong to patients.
        if (!PasswordAccepted(body.Password, account, config) || account is null)
            return Results.Ok(new ErrorResponse { Message = "Invalid credentials" });

        if (IsImplausibleFamilyGroup(account, config))
        {
            // Same reply as a bad login: which numbers are placeholders is not the caller's business.
            loggerFactory.CreateLogger("MedVault.Login").LogWarning(
                "Refused login for a number linked to {Count} patients - looks like a placeholder, not a family",
                account.LinkedPatientIds.Count);

            return Results.Ok(new ErrorResponse { Message = "Invalid credentials" });
        }

        return Results.Ok(new LoginResponse
        {
            Success = true,
            Token = tokens.Issue(account),
            User = account.Mobile,
            Name = account.Name,
            Lab = config["Facility:DisplayName"] ?? "MedVault Demo Hospital",
        });
    }

    /// <summary>
    /// Auth:RequirePassword=false reproduces the MedVault behaviour exactly: the mobile number alone is
    /// enough. That is how the captured API works today, and it means anyone who knows a patient's
    /// number can read their results - see spec section 3, open item 1. Turn it on once the hospital
    /// has issued passwords.
    /// </summary>
    /// <remarks>
    /// Takes a nullable account on purpose. When no account was found we still verify, against a
    /// throwaway hash, so an unknown number costs the same time as a known one - see
    /// <see cref="PasswordHasher.DummyHash"/>. The caller checks for null separately.
    /// </remarks>
    private static bool PasswordAccepted(string? supplied, HisAccount? account, IConfiguration config)
    {
        if (!config.GetValue("Auth:RequirePassword", true)) return true;

        var stored = account?.PasswordHash ?? PasswordHasher.DummyHash;
        var verified = !string.IsNullOrEmpty(supplied) && PasswordHasher.Verify(supplied, stored);

        return verified && account is not null;
    }

    /// <summary>
    /// One mobile number legitimately covers a family - the MedVault capture has three patients on one
    /// number. It does not cover thousands. The Bekaa HIS has a single number linked to 11,989 patient
    /// records, and several more above 40: default values, the hospital's own number, a clerk's number
    /// typed onto every walk-in. Since the number alone logs in (Auth:RequirePassword=false), each of
    /// those is a key to every record behind it.
    ///
    /// A number over the limit is refused rather than truncated. Returning the first N would hand out
    /// a real patient's results to whoever holds a placeholder number, and silently hiding the rest
    /// would look like working software.
    ///
    /// Set Auth:MaxLinkedPatients to 0 to disable the check.
    /// </summary>
    private static bool IsImplausibleFamilyGroup(HisAccount account, IConfiguration config)
    {
        var max = config.GetValue("Auth:MaxLinkedPatients", 12);
        return max > 0 && account.LinkedPatientIds.Count > max;
    }

    private static IResult Logout(HttpContext ctx, ITokenService tokens)
    {
        tokens.Revoke(ReadToken(ctx));
        return Results.Ok(new { success = true });
    }

    // ---------- API 2 ----------

    private static async Task<IResult> GetRequestsAsync(
        HttpContext ctx, IHisRepository his, ITokenService tokens, PdfFileResolver pdfs, CancellationToken ct)
    {
        if (tokens.Validate(ReadToken(ctx)) is not { } account) return Unauthorized();

        var requests = await his.GetRequestsAsync(account, ct);

        // The PDFs are on disk, not in the HIS - the SQL cannot fill pdf_path in, so it arrives null
        // from every repository and is resolved here instead. Doing it in the endpoint rather than in
        // each repository keeps mock and sql identical, and keeps the disk lookup out of the code path
        // that GetResultsAsync uses for its ownership check.
        //
        // A repository that does know the path (a future HIS column) wins: only null is filled in.
        // pdf_paths is always filled, since nothing else supplies it.
        requests = [.. requests.Select(r =>
        {
            var onDisk = pdfs.PathsForRequest(r.RequestNb);

            return r with
            {
                PdfPath = r.PdfPath ?? onDisk.FirstOrDefault(),
                PdfPaths = onDisk,
            };
        })];

        return Results.Ok(new RequestsResponse { Requests = requests });
    }

    // ---------- API 3 ----------

    private static async Task<IResult> GetResultsAsync(
        HttpContext ctx,
        [FromQuery] string? requestnb,
        IHisRepository his,
        ITokenService tokens,
        CancellationToken ct)
    {
        if (tokens.Validate(ReadToken(ctx)) is not { } account) return Unauthorized();
        if (string.IsNullOrWhiteSpace(requestnb)) return BadRequest();

        var results = await his.GetResultsAsync(account, requestnb, ct);
        if (results is null) return NotFound();

        return Results.Ok(new ResultsResponse { RequestNb = requestnb, Results = results });
    }

    // ---------- API 4 ----------

    private static async Task<IResult> GetRadiologyAsync(
        HttpContext ctx, IHisRepository his, ITokenService tokens, CancellationToken ct)
    {
        if (tokens.Validate(ReadToken(ctx)) is not { } account) return Unauthorized();

        var studies = await his.GetRadiologyStudiesAsync(account, ct);
        return Results.Ok(new RadiologyListResponse { Studies = studies });
    }

    // ---------- API 5 ----------

    private static async Task<IResult> GetRadiologyReportAsync(
        HttpContext ctx,
        [FromQuery] string? studynb,
        IHisRepository his,
        ITokenService tokens,
        CancellationToken ct)
    {
        if (tokens.Validate(ReadToken(ctx)) is not { } account) return Unauthorized();
        if (string.IsNullOrWhiteSpace(studynb)) return BadRequest();

        var report = await his.GetRadiologyReportAsync(account, studynb, ct);
        if (report is null) return NotFound();

        return Results.Ok(new RadiologyReportResponse { StudyNb = studynb, Report = report });
    }

    // ---------- API 6 ----------

    private static async Task<IResult> GetFileAsync(
        HttpContext ctx,
        [FromQuery] string? path,
        IHisRepository his,
        ITokenService tokens,
        PdfFileResolver resolver,
        CancellationToken ct)
    {
        if (tokens.Validate(ReadToken(ctx)) is not { } account) return Unauthorized();
        if (string.IsNullOrWhiteSpace(path)) return BadRequest();

        // Ownership first, then path safety. Both must pass, and a failure of either looks identical
        // from outside - a 403 would confirm the file exists.
        if (!await his.OwnsFileAsync(account, path, ct)) return NotFound();

        var full = resolver.Resolve(path);
        if (full is null) return NotFound();

        return Results.File(full, "application/pdf", Path.GetFileName(full));
    }

    // ---------- Shared ----------

    /// <summary>
    /// tcToken in the query string is the MedVault contract and what the app sends today. Authorization:
    /// Bearer is accepted too so the app can migrate off query-string credentials without a server
    /// change - query strings end up in IIS and proxy logs (spec section 9, rule 2).
    /// </summary>
    private static string? ReadToken(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();

        return ctx.Request.Query["tcToken"].FirstOrDefault();
    }

    private static IResult Unauthorized() =>
        Results.Json(new ErrorResponse { Message = "Unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult NotFound() =>
        Results.Json(new ErrorResponse { Message = "Not found" }, statusCode: StatusCodes.Status404NotFound);

    private static IResult BadRequest() =>
        Results.Json(new ErrorResponse { Message = "Bad request" }, statusCode: StatusCodes.Status400BadRequest);
}
