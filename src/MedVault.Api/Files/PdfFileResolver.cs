using Microsoft.Extensions.FileProviders;

namespace MedVault.Api.Files;

/// <summary>
/// Turns a pdf_path from the HIS into a real file path, or refuses.
///
/// pdf_path values look like "ResultPDF/26L0250003_1.pdf" - relative, and the MedVault document never says
/// what they are relative to. Files:Root supplies that: the FoxPro 9 system writes every generated
/// report straight into that folder (C:\ExcelH), flat, no subfolders.
///
/// Two ways out of this class:
///   * <see cref="Resolve"/> - used by the token-checked /api/mobile/file route, which also runs an
///     ownership check in the repository before it gets here.
///   * <see cref="PathForRequest"/> - finds the report belonging to a request number and returns the
///     public URL under <see cref="UrlPrefix"/>, which Program.cs serves statically. That route has
///     NO token and NO ownership check: anyone who can reach the port and guess a filename can read
///     the report. Acceptable while the app is bound to localhost; see README before exposing it.
/// </summary>
public sealed class PdfFileResolver(IConfiguration config)
{
    /// <summary>URL path the root folder is served under, and the prefix written into pdf_path.</summary>
    public const string UrlPrefix = "ResultPDF";

    private readonly string? _root = config["Files:Root"];

    /// <summary>Null when Files:Root is unset or the folder does not exist.</summary>
    public string? RootDirectory =>
        !string.IsNullOrWhiteSpace(_root) && Directory.Exists(_root) ? Path.GetFullPath(_root) : null;

    /// <summary>
    /// Null when the path escapes the root, is absolute, or does not exist.
    /// Callers must treat every null the same way (404) so that "no such file" and "not yours" are
    /// indistinguishable from outside.
    /// </summary>
    public string? Resolve(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(_root) || string.IsNullOrWhiteSpace(relativePath))
            return null;

        // Reject before normalising: Path.GetFullPath would happily resolve "../.." into a real path,
        // and a rooted candidate silently discards the root we are trying to confine it to.
        if (relativePath.Contains("..", StringComparison.Ordinal)) return null;
        if (relativePath.Contains('\0')) return null;
        if (Path.IsPathRooted(relativePath)) return null;
        if (relativePath.StartsWith('/') || relativePath.StartsWith('\\')) return null;
        if (relativePath.Contains(':')) return null;   // drive letters and NTFS alternate data streams

        // pdf_path is a URL path (for example, ResultPDF/26X007397_102.PDF), while Files:Root
        // points at the flat directory containing the PDFs. ResultPDF is the URL mount point,
        // not a physical subdirectory under C:\\ExcelH.
        var filePath = relativePath.Replace('\\', '/');
        var prefix = UrlPrefix + "/";
        if (filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            filePath = filePath[prefix.Length..];
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root));
        var candidate = Path.GetFullPath(Path.Combine(rootFull, filePath));

        // Belt and braces: confirm the resolved path really is inside the root, so a form we did not
        // anticipate still cannot escape.
        if (!candidate.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;

        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Every report on disk for one request number, as "ResultPDF/&lt;filename&gt;.pdf", or an empty list
    /// when FoxPro has written none yet. Empty is the normal state for a pending order, not an error.
    ///
    /// One visit routinely produces several files. On the Bekaa server they are named
    /// &lt;request_nb&gt;_1NN.PDF, where NN looks like the laboratory category code - 26L007599_101.PDF and
    /// 26L007599_107.PDF alongside each other, with _100 the usual single-file case. All of them are
    /// returned, ordered by filename, so _100 comes first and the app can show every section rather
    /// than one arbitrary pick.
    ///
    /// Prefix rather than *&lt;request_nb&gt;*: a contains-match would let request 26L00618 pick up
    /// 26L006183's report, and every file in C:\ExcelH starts with its request number anyway.
    /// The glob is case-insensitive on Windows, so the uppercase .PDF the HIS writes matches.
    /// </summary>
    public IReadOnlyList<string> PathsForRequest(string? requestNb)
    {
        if (RootDirectory is not { } root) return [];
        if (string.IsNullOrWhiteSpace(requestNb)) return [];

        // The request number goes into a glob, so it must not be able to carry wildcards or separators
        // of its own. Visit ids are alphanumeric; anything else is not a request number we can match.
        // Trimmed first: the HIS stores ADMI padded, so request_nb can arrive with trailing spaces.
        var key = requestNb.Trim();
        if (!key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return [];

        var found = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, $"{key}*.pdf", SearchOption.TopDirectoryOnly))
        {
            // The glob is a prefix match, so request 26L007599 also matches 26L0075990's report - a
            // different visit whose number merely starts the same way. Require what follows the
            // number to be a separator or the extension, never another digit or letter.
            var rest = Path.GetFileName(path)[key.Length..];
            if (rest.Length > 0 && char.IsAsciiLetterOrDigit(rest[0])) continue;

            // FoxPro creates the file before it writes it, so a zero-length PDF is a report that is
            // still being generated (or one whose generation failed). Both read as "not ready yet" -
            // better than handing the app a file that opens as a corrupt document.
            if (new FileInfo(path).Length == 0) continue;

            found.Add(Path.GetFileName(path));
        }

        // Ordinal, so the order is the same whatever the server's locale: _100, _101, _107.
        found.Sort(StringComparer.OrdinalIgnoreCase);

        return [.. found.Select(name => $"{UrlPrefix}/{name}")];
    }

    /// <summary>
    /// The single pdf_path the MedVault contract carries. The first of <see cref="PathsForRequest"/>, which
    /// is the _100 combined report when the HIS wrote one, or null when there is nothing on disk.
    /// Kept because pdf_path is a single string in the contract the mobile app already reads.
    /// </summary>
    public string? PathForRequest(string? requestNb) =>
        PathsForRequest(requestNb).FirstOrDefault();

    /// <summary>The folder to serve statically, or null when it is not configured or not present.</summary>
    public PhysicalFileProvider? CreateFileProvider() =>
        RootDirectory is { } root ? new PhysicalFileProvider(root) : null;
}
