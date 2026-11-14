using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.ContentImport.Contracts;

namespace Atlas.Server.Features.ContentImport.Domain;

public static class WikiJsArchive
{
    public const long MaximumArchiveBytes = 64L * 1024 * 1024;
    public const long MaximumExpandedBytes = 256L * 1024 * 1024;
    public const long MaximumMarkdownBytes = 16L * 1024 * 1024;
    public const int MaximumPageBytes = 1_000_000;

    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static WikiImportOptions Validate(WikiImportOptions input)
    {
        var key = Rules.Required(input.SourceKey, "Identifikátor zdroje", 80).ToLowerInvariant();
        if (!Regex.IsMatch(key, "^[a-z0-9][a-z0-9._-]*$"))
        {
            throw new ApiException(400, "Identifikátor zdroje smí obsahovat písmena a-z, čísla, tečku, pomlčku a podtržítko.");
        }
        var prefix = string.IsNullOrWhiteSpace(input.StripPrefix) ? null : SafePath(input.StripPrefix.Trim().TrimEnd('/')) + "/";
        if (input.SourceBaseUrl is { Length: > 0 } url && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0))
        {
            throw new ApiException(400, "Původní URL musí být HTTP(S) adresa bez přihlašovacích údajů, dotazu a fragmentu.");
        }
        return input with { SourceKey = key, SpaceName = Rules.Required(input.SpaceName, "Název karanténního prostoru"), StripPrefix = prefix };
    }

    public static string SafePath(string value)
    {
        return Normalize(value) ?? throw new ApiException(400, "ZIP obsahuje nebezpečnou nebo nejednoznačnou cestu.");
    }

    /// <summary>
    /// The folder every entry of the ZIP sits in, as a repository downloaded from GitHub or zipped as a
    /// folder has it; <c>null</c> when a file lies at the top level or the ZIP cannot be read. The stream position is kept.
    /// </summary>
    public static string? SingleRoot(Stream input)
    {
        var start = input.Position;
        try
        {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
            string? root = null;
            var files = false;
            foreach (var entry in zip.Entries)
            {
                var slash = entry.FullName.IndexOf('/');
                if (slash <= 0 || root is not null && !entry.FullName.AsSpan(0, slash).SequenceEqual(root))
                {
                    return null;
                }
                root ??= entry.FullName[..slash];
                files |= !entry.FullName.EndsWith('/');
            }
            return files && root is not null ? Normalize(root) : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        finally
        {
            input.Position = start;
        }
    }

    public static async Task<WikiSourceArchive> ReadAsync(Stream input, WikiImportOptions options, CancellationToken ct = default)
    {
        if (!input.CanSeek || input.Length > MaximumArchiveBytes)
        {
            throw new ApiException(413, "ZIP smí mít nejvýše 64 MiB.");
        }
        try
        {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
            if (zip.Entries.Count > 1000)
            {
                throw new ApiException(413, "ZIP smí obsahovat nejvýše 1000 položek.");
            }
            // An entry the import cannot take as it is (unsafe name, symbolic link) is reported, not a reason to reject the export.
            var excluded = new Dictionary<string, string>(StringComparer.Ordinal);
            var entries = new List<(ZipArchiveEntry Entry, string Path)>();
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }
                var path = Normalize(entry.FullName);
                if (path is null)
                {
                    excluded.TryAdd(Printable(entry.FullName), "Nebezpečná nebo neplatná cesta v ZIP; položka byla vynechána.");
                    continue;
                }
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    excluded.TryAdd(path, "Položka ZIP je symbolický odkaz; byla vynechána.");
                    continue;
                }
                if (options.StripPrefix is { } prefix)
                {
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Length == prefix.Length)
                    {
                        continue;
                    }
                    path = path[prefix.Length..];
                }
                entries.Add((entry, path));
            }
            // Atlas paths ignore letter case. The first spelling (ordinal order) keeps the path, the others get a numbered one.
            var taken = entries.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
            var renamedFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var files = new Dictionary<string, WikiSourceFile>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var (entry, original) in entries.OrderBy(x => x.Path, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var path = original;
                if (!assigned.Add(path))
                {
                    for (var number = 2; taken.Contains(path) || !assigned.Add(path); number++)
                    {
                        path = Numbered(original, number);
                    }
                    Rename(renamed, original, path);
                    renamedFrom[path] = original;
                }
                total += entry.Length;
                if (entry.Length > AttachmentFiles.MaximumFileBytes || total > MaximumExpandedBytes || entry.Length > Math.Max(1, entry.CompressedLength) * 200)
                {
                    throw new ApiException(413, "ZIP překračuje bezpečný limit rozbalených dat nebo komprese.");
                }
                await using var source = entry.Open();
                using var buffer = new MemoryStream();
                var chunk = new byte[65536];
                while (true)
                {
                    var count = await source.ReadAsync(chunk, ct);
                    if (count == 0)
                    {
                        break;
                    }
                    if (buffer.Length + count > entry.Length)
                    {
                        throw new ApiException(413, "Skutečná velikost ZIP neodpovídá hlavičce.");
                    }
                    await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
                }
                if (buffer.Length != entry.Length)
                {
                    throw new ApiException(400, "ZIP je neúplný.");
                }
                var bytes = buffer.ToArray();
                files.Add(path, new WikiSourceFile(path, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes))));
            }
            var pages = new List<WikiSourcePage>();
            var skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long markdownBytes = 0;
            foreach (var file in files.Values.Where(f => f.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
            {
                markdownBytes += file.Bytes.LongLength;
                if (markdownBytes > MaximumMarkdownBytes)
                {
                    throw new ApiException(413, "Markdown obsah jednoho importu smí mít celkem nejvýše 16 MiB.");
                }
                if (file.Bytes.Length > MaximumPageBytes)
                {
                    // One oversized article is reported, not a reason to reject the whole export.
                    skipped[file.Path] = "Markdown stránka přesahuje 1 MB; originál zůstává v ZIP.";
                    continue;
                }
                if (Read(file, skipped) is not { } page)
                {
                    continue;
                }
                AddPage(file.Path, file, page.Header, [], false);
            }
            // Pages written in the Wiki.js visual editor are dumped as HTML. They take the same page
            // identity a Markdown page would, so switching the source editor later updates instead of duplicating.
            foreach (var file in files.Values.Where(f => f.Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
            {
                var pagePath = file.Path[..^".html".Length] + ".md";
                if (files.ContainsKey(pagePath))
                {
                    skipped[file.Path] = "Stránka má vlastní Markdown; HTML zůstává jen jako zachovaný originál v ZIP.";
                    continue;
                }
                if (Read(file, skipped) is not (var text, var header))
                {
                    continue;
                }
                // Wiki.js writes its metadata comment into every page; a complete HTML document without it is an uploaded file.
                if (!text.StartsWith("<!--\n", StringComparison.Ordinal) && IsHtmlDocument(text))
                {
                    continue;
                }
                var converted = WikiHtmlToMarkdown.Convert(header.Body);
                if (converted.Markdown.Length == 0)
                {
                    skipped[file.Path] = "Stránka neobsahuje obsah převeditelný do Markdownu; originál zůstává v ZIP.";
                    continue;
                }
                var size = Utf8.GetByteCount(converted.Markdown);
                if (size > MaximumPageBytes)
                {
                    skipped[file.Path] = "Převedená stránka by přesáhla 1 MB; originál zůstává v ZIP.";
                    continue;
                }
                markdownBytes += size;
                if (markdownBytes > MaximumMarkdownBytes)
                {
                    throw new ApiException(413, "Markdown obsah jednoho importu smí mít celkem nejvýše 16 MiB.");
                }
                AddPage(pagePath, file, header with { Body = converted.Markdown }, converted.Findings, true);
            }
            return new WikiSourceArchive(files, pages, skipped) { Excluded = excluded, Renamed = renamed, StrippedPrefix = options.StripPrefix };

            void AddPage(string path, WikiSourceFile source, WikiPageHeader header, IReadOnlyList<WikiImportFinding> extra, bool html)
            {
                // A leading H1 is the page title: it names an untitled page, and repeats a titled one.
                var title = header.Title;
                var leading = WikiMarkdownConverter.LeadingHeading(header.Body);
                var drop = leading is { Length: > 0 } && (!header.HasTitle || string.Equals(leading, header.Title.Trim(), StringComparison.OrdinalIgnoreCase));
                if (drop && !header.HasTitle)
                {
                    title = leading!.Length <= 200 ? leading : leading[..200];
                }
                string[] warnings = renamedFrom.TryGetValue(source.Path, out var original)
                    ? ["Cesta „" + original + "“ se od jiné položky ZIP liší jen velikostí písmen; stránka je importována jako „" + path + "“."]
                    : [];
                pages.Add(new WikiSourcePage(path, Rules.Required(Rules.CleanLine(title), "Název importované stránky"), header.Body, source.Sha256, warnings, source.Path)
                {
                    Findings = [.. header.Findings, .. extra],
                    Tags = header.Tags,
                    Aliases = header.Aliases,
                    DropLeadingHeading = drop,
                    LineOffset = html ? 0 : header.LineOffset,
                    SourceIsHtml = html,
                });
                if (pages.Count > 500)
                {
                    throw new ApiException(413, "Jeden import smí obsahovat nejvýše 500 stránek.");
                }
            }
        }
        catch (InvalidDataException)
        {
            throw new ApiException(400, "ZIP je poškozený, šifrovaný nebo nepodporovaný.");
        }
    }

    /// <summary>The NFC form of a relative archive path (ZIPs made on macOS store decomposed names), or <c>null</c> when it could leave the import.</summary>
    private static string? Normalize(string value)
    {
        if (value.Length is 0 or > 1024 || value.StartsWith('/') || value.Contains('\\') || value.Contains(':') || value.Any(char.IsControl))
        {
            return null;
        }
        try
        {
            var path = value.Normalize(NormalizationForm.FormC);
            return path.Split('/').Any(x => x is "" or "." or "..") ? null : path;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Printable(string value)
    {
        var text = new string(value.Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return text.Length <= 200 ? text : text[..200] + "…";
    }

    /// <summary><c>docs/Page.md</c> numbered as <c>docs/Page-2.md</c>.</summary>
    private static string Numbered(string path, int number)
    {
        var slash = path.LastIndexOf('/') + 1;
        var extension = Path.GetExtension(path[slash..]);
        var stem = path[..^extension.Length];
        return stem.Length == slash ? path + "-" + number : stem + "-" + number + extension;
    }

    private static void Rename(Dictionary<string, string> renamed, string original, string path)
    {
        renamed.TryAdd(original, path);
        if (original.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || original.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            var from = original[..original.LastIndexOf('.')];
            var to = path[..path.LastIndexOf('.')];
            renamed.TryAdd(from, to);
            renamed.TryAdd(from + ".md", to + ".md");
        }
    }

    /// <summary>The page text and header, or <c>null</c> with the reason recorded when the file is not valid UTF-8 or its metadata cannot be read.</summary>
    private static (string Text, WikiPageHeader Header)? Read(WikiSourceFile file, Dictionary<string, string> skipped)
    {
        try
        {
            var text = Decode(file);
            return (text, WikiPageMetadata.Read(text, PageName(file)));
        }
        catch (DecoderFallbackException)
        {
            skipped[file.Path] = "Stránka není v kódování UTF-8; originál zůstává v ZIP.";
        }
        catch (ApiException error) when (error.Status == 400)
        {
            skipped[file.Path] = error.Message + " Originál zůstává v ZIP.";
        }
        return null;
    }

    private static bool IsHtmlDocument(string text)
    {
        var start = text.AsSpan().TrimStart();
        return start.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    private static string Decode(WikiSourceFile file)
    {
        return Utf8.GetString(file.Bytes).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string PageName(WikiSourceFile file)
    {
        return Path.GetFileNameWithoutExtension(file.Path);
    }
}
