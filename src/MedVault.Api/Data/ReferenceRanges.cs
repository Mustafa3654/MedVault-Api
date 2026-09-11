using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MedVault.Api.Contracts;

namespace MedVault.Api.Data;

/// <summary>
/// Reference ranges for the Bekaa HIS, supplied as a hand-maintained catalogue file.
///
/// Ranges are not stored anywhere on that server - every column matching norm/mini/maxi/refer/unit
/// was searched across all 120 databases and came back empty, and TRLAB.REFER holds the analyser name
/// (Sysmex, AU480...), not a range. So <c>ref_range</c> could only ever be null from SQL. This class
/// is the seam that fills it: the lab signs off a catalogue once, and every result row is annotated
/// from it on the way out.
///
/// The same catalogue also produces <c>result_status</c>. TRLAB.ABNORMAL is NULL in all 242,297 rows,
/// so the analyser flag is not available either - H/L/N is computed here by comparing the numeric
/// result against the range. When a test has no catalogue entry both fields stay null, which the
/// mobile app reads as "this lab does not publish a range" and hides the gauge. Emitting "N" for an
/// unknown test would tell the patient a CRP of 60 is normal, so nothing is ever guessed.
/// </summary>
public sealed class ReferenceRanges
{
    private readonly Dictionary<string, RangeEntry> _byCode;
    private readonly Dictionary<string, RangeEntry> _byTestName;

    public ReferenceRanges(IWebHostEnvironment env, IConfiguration config, ILogger<ReferenceRanges> logger)
    {
        _byCode = new Dictionary<string, RangeEntry>(StringComparer.OrdinalIgnoreCase);
        _byTestName = new Dictionary<string, RangeEntry>(StringComparer.OrdinalIgnoreCase);

        var configured = config["His:ReferenceRangeCatalogue"];
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "Data", "Catalogue", "reference-ranges.json")
            : Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);

        if (!File.Exists(path))
        {
            // Not fatal. Without a catalogue the API behaves exactly as it did before - ranges null,
            // status null - and the app hides the gauge rather than showing a wrong one.
            logger.LogWarning("Reference range catalogue not found at {Path}; ref_range and result_status will be null", path);
            return;
        }

        try
        {
            var file = JsonSerializer.Deserialize<CatalogueFile>(File.ReadAllText(path), JsonOptions);
            foreach (var entry in file?.Tests ?? [])
            {
                if (entry.Code is { Length: > 0 } code) _byCode[code.Trim()] = entry;
                if (entry.TestName is { Length: > 0 } name) _byTestName[Normalise(name)] = entry;
            }

            logger.LogInformation(
                "Loaded {Codes} reference ranges by code and {Names} by test name from {Path}",
                _byCode.Count, _byTestName.Count, path);
        }
        catch (Exception ex)
        {
            // A malformed catalogue must not take the API down mid-clinic: log it and serve nulls.
            logger.LogError(ex, "Failed to read reference range catalogue {Path}", path);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Returns <paramref name="item"/> with ref_range, result_status and sign filled in from the
    /// catalogue. Values the HIS itself supplied win - if a future schema change starts returning a
    /// real range or a real analyser flag, that is better than anything computed here.
    /// </summary>
    public ResultItem Annotate(ResultItem item)
    {
        if (item.RefRange is { Length: > 0 } && item.ResultStatus is { Length: > 0 }) return item;
        if (Lookup(item) is not { } entry) return item;

        var status = item.ResultStatus is { Length: > 0 } ? item.ResultStatus : Classify(item.Result, entry);

        return item with
        {
            RefRange = item.RefRange is { Length: > 0 } ? item.RefRange : entry.Display(),
            ResultStatus = status,
            Sign = item.Sign is { Length: > 0 } ? item.Sign : status switch
            {
                "H" => "High",
                "L" => "Low",
                _ => item.Sign,
            },
        };
    }

    private RangeEntry? Lookup(ResultItem item)
    {
        // field_num carries TRLAB.CODE, which is the stable key. Test name is the fallback so a
        // catalogue can be started from a report printout before the codes have been collected.
        if (item.FieldNum is { Length: > 0 } code && _byCode.TryGetValue(code.Trim(), out var byCode))
            return byCode;

        return _byTestName.TryGetValue(Normalise(item.TestName), out var byName) ? byName : null;
    }

    /// <summary>
    /// H / L / N, or null when the comparison cannot be made honestly - a textual result
    /// ("NEGATIVE", "+++"), or a catalogue entry that carries only display text and no bounds.
    /// </summary>
    private static string? Classify(string? result, RangeEntry entry)
    {
        if (entry is { Low: null, High: null }) return null;

        var text = result?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        // Results are sometimes reported as "<0.5" or "> 200". The bound itself is the value to
        // compare, and the operator already tells us which side of the range it falls on.
        var open = text[0] is '<' or '>' ? text[0] : (char?)null;
        if (open is not null) text = text[1..].Trim();

        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;

        if (entry.Low is { } low && (value < low || (open == '<' && value <= low))) return "L";
        if (entry.High is { } high && (value > high || (open == '>' && value >= high))) return "H";
        return "N";
    }

    private static string Normalise(string? name) =>
        string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();

    private sealed record CatalogueFile
    {
        [JsonPropertyName("tests")] public List<RangeEntry>? Tests { get; init; }
    }

    public sealed record RangeEntry
    {
        /// <summary>TRLAB.CODE, matched against the row's field_num. Preferred over TestName.</summary>
        [JsonPropertyName("code")] public string? Code { get; init; }

        /// <summary>Matched case- and whitespace-insensitively against test_name.</summary>
        [JsonPropertyName("test_name")] public string? TestName { get; init; }

        [JsonPropertyName("low")] public decimal? Low { get; init; }
        [JsonPropertyName("high")] public decimal? High { get; init; }

        /// <summary>
        /// Overrides the rendered range. Use for anything low/high cannot express - "Negative",
        /// "&lt; 5", sex-specific text. When set alone, the gauge shows the text and no H/L is computed.
        /// </summary>
        [JsonPropertyName("text")] public string? Text { get; init; }

        /// <summary>
        /// The app renders ref_range as HTML, and the MedVault capture wraps it in a div
        /// ("&lt;div&gt;70-180&lt;/div&gt;"). Matching that keeps Bekaa and Verdun rows identical in the app.
        /// </summary>
        public string? Display()
        {
            var body = Text switch
            {
                { Length: > 0 } t => t,
                _ => (Low, High) switch
                {
                    ({ } l, { } h) => $"{Trim(l)}-{Trim(h)}",
                    (null, { } h) => $"&lt; {Trim(h)}",
                    ({ } l, null) => $"&gt; {Trim(l)}",
                    _ => null,
                },
            };

            return body is null ? null : $"<div>{body}</div>";
        }

        private static string Trim(decimal d) =>
            d.ToString("0.############", CultureInfo.InvariantCulture);
    }
}
