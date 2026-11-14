using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Atlas.Server.Features.ContentImport.Contracts;

namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>
/// Resolves links between the pages and files of one import. Markdown destinations resolve by path;
/// wiki links (<c>[[Page]]</c>) additionally by file name, title and aliases, case-insensitively and
/// with spaces, hyphens and underscores treated alike. Fragments map to the target page's headings by
/// GitHub-style slug or heading text, so the link lands on the editor's heading identity.
/// </summary>
public sealed class ImportLinkResolver
{
    private const int PathMatch = 4;
    private const int FileNameMatch = 3;
    private const int TitleMatch = 2;
    private const int FoldedMatch = 1;

    private readonly WikiSourceArchive m_archive;
    private readonly IReadOnlyDictionary<string, Guid> m_ids;
    private readonly WikiImportOptions m_options;
    private readonly Dictionary<string, List<WikiSourcePage>> m_pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<WikiSourceFile>> m_files = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, IReadOnlyList<(string Text, string Id)>> m_outlines;

    /// <param name="ids">The page identity of every source page that will be created, keyed by path.</param>
    /// <param name="outlines">Each page's headings (<see cref="WikiMarkdownConverter.Outline"/>) keyed by path, so a link may target a heading of any page.</param>
    public ImportLinkResolver(WikiSourceArchive archive, IReadOnlyList<WikiSourcePage> sources, IReadOnlyDictionary<string, Guid> ids, WikiImportOptions options,
        IReadOnlyDictionary<string, IReadOnlyList<(string Text, string Id)>> outlines)
    {
        m_archive = archive;
        m_ids = ids;
        m_options = options;
        m_outlines = outlines;
        foreach (var page in sources)
        {
            var path = ImportPlanner.RemoveExtension(page.Path);
            var name = path[(path.LastIndexOf('/') + 1)..];
            Index(m_pages, "p:" + path.ToLowerInvariant(), page);
            Index(m_pages, "f:" + name.ToLowerInvariant(), page);
            foreach (var title in page.Aliases.Prepend(page.Title))
            {
                Index(m_pages, "t:" + title.Trim().ToLowerInvariant(), page);
                Index(m_pages, "n:" + Fold(title), page);
            }
            Index(m_pages, "n:" + Fold(name), page);
            Index(m_pages, "n:" + Fold(path), page);
        }
        foreach (var file in archive.Files.Values.Where(f => !f.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            Index(m_files, "f:" + file.Path[(file.Path.LastIndexOf('/') + 1)..].ToLowerInvariant(), file);
        }
    }

    /// <summary>The links of one page; attachments it references are collected into <paramref name="assets"/>.</summary>
    public WikiPageLinks For(WikiSourcePage source, Dictionary<Guid, WikiSourceFile> assets)
    {
        return new WikiPageLinks((url, image) => Url(source, url, image, assets),
            (target, alias, embed) => Wiki(source, target, alias, embed, assets),
            headingId => PageHref(m_ids[source.Path], headingId));
    }

    public static string PageHref(Guid pageId, string? headingId = null)
    {
        return "/#/pages/" + pageId.ToString("D") + (headingId is null ? "" : "?heading=" + Uri.EscapeDataString(headingId));
    }

    /// <summary>GitHub's anchor algorithm: lower case, punctuation removed, spaces become hyphens.</summary>
    public static string Slug(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_')
            {
                builder.Append(character);
            }
            else if (char.IsWhiteSpace(character))
            {
                builder.Append('-');
            }
        }
        return builder.ToString();
    }

    /// <summary>Case, diacritics and separator insensitive form used for wiki names and heading text.</summary>
    public static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        var separator = false;
        foreach (var character in text.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (char.IsWhiteSpace(character) || character is '-' or '_')
            {
                separator = builder.Length > 0;
                continue;
            }
            if (separator)
            {
                builder.Append('-');
                separator = false;
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static void Index<T>(Dictionary<string, List<T>> index, string key, T value)
    {
        if (key.Length <= 2)
        {
            return;
        }
        if (!index.TryGetValue(key, out var list))
        {
            index[key] = list = [];
        }
        if (!list.Contains(value))
        {
            list.Add(value);
        }
    }

    /// <summary>
    /// The best page for a name. <c>Ambiguous</c> is set when another page matches equally well and the
    /// choice is not settled by the linking page's folder.
    /// </summary>
    private (WikiSourcePage Page, int Quality, bool Ambiguous)? FindPage(WikiSourcePage source, string target)
    {
        var value = ImportPlanner.RemoveExtension(target.Trim().Trim('/'));
        if (value.Length == 0)
        {
            return null;
        }
        var folder = ImportPlanner.ParentPath(source.Path);
        var name = value[(value.LastIndexOf('/') + 1)..];
        var candidates = new List<(WikiSourcePage Page, int Quality)>();
        void Add(string key, int quality)
        {
            if (m_pages.TryGetValue(key, out var pages))
            {
                candidates.AddRange(pages.Select(page => (page, quality)));
            }
        }
        if (folder.Length > 0)
        {
            Add("p:" + (folder + "/" + value).ToLowerInvariant(), PathMatch);
        }
        Add("p:" + value.ToLowerInvariant(), PathMatch);
        Add("f:" + name.ToLowerInvariant(), FileNameMatch);
        Add("t:" + value.ToLowerInvariant(), TitleMatch);
        Add("n:" + Fold(value), FoldedMatch);
        Add("n:" + Fold(name), FoldedMatch);
        if (candidates.Count == 0)
        {
            return null;
        }
        var best = candidates.Max(x => x.Quality);
        var equal = candidates.Where(x => x.Quality == best).Select(x => x.Page).Distinct().ToList();
        var chosen = equal
            .OrderBy(x => ImportPlanner.ParentPath(x.Path).Equals(folder, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.Path.Length)
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .First();
        return (chosen, best, equal.Count > 1 && !ImportPlanner.ParentPath(chosen.Path).Equals(folder, StringComparison.OrdinalIgnoreCase));
    }

    private (WikiSourceFile File, bool Ambiguous)? FindFile(WikiSourcePage source, string target)
    {
        var value = target.Trim().TrimStart('/');
        var folder = ImportPlanner.ParentPath(source.Path);
        if (folder.Length > 0 && m_archive.Files.TryGetValue(folder + "/" + value, out var relative) && !relative.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return (relative, false);
        }
        if (m_archive.Files.TryGetValue(value, out var rooted) && !rooted.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return (rooted, false);
        }
        if (!m_files.TryGetValue("f:" + value[(value.LastIndexOf('/') + 1)..].ToLowerInvariant(), out var named))
        {
            return null;
        }
        var chosen = named.OrderBy(x => ImportPlanner.ParentPath(x.Path).Equals(folder, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.Path.Length).ThenBy(x => x.Path, StringComparer.Ordinal).First();
        return (chosen, named.Count > 1 && !ImportPlanner.ParentPath(chosen.Path).Equals(folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A fragment against a page's headings: GitHub anchor (slug with occurrence), then slugged or folded heading text.</summary>
    private (string? HeadingId, string? Finding) FindHeading(string path, string fragment)
    {
        string anchor;
        try
        {
            anchor = Uri.UnescapeDataString(fragment).Trim();
        }
        catch (UriFormatException)
        {
            anchor = fragment.Trim();
        }
        if (anchor.Length == 0)
        {
            return (null, null);
        }
        if (anchor.StartsWith('^'))
        {
            return (null, "block-reference");
        }
        if (!m_outlines.TryGetValue(path, out var headings) || headings.Count == 0)
        {
            return (null, "anchor-unresolved");
        }
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var slugs = headings.Select(heading =>
        {
            var slug = Slug(heading.Text);
            var count = seen.GetValueOrDefault(slug);
            seen[slug] = count + 1;
            return count == 0 ? slug : slug + "-" + count.ToString(CultureInfo.InvariantCulture);
        }).ToArray();
        var lower = anchor.ToLowerInvariant();
        var slugged = Slug(anchor);
        var folded = Fold(anchor);
        for (var pass = 0; pass < 3; pass++)
        {
            for (var index = 0; index < headings.Count; index++)
            {
                var match = pass switch
                {
                    0 => slugs[index] == lower,
                    1 => slugs[index] == slugged,
                    _ => Fold(headings[index].Text) == folded,
                };
                if (match)
                {
                    return (headings[index].Id, null);
                }
            }
        }
        return (null, "anchor-unresolved");
    }

    private string Attachment(WikiSourcePage source, WikiSourceFile file, bool image, Dictionary<Guid, WikiSourceFile> assets)
    {
        // File IDs include page, source path and hash, so repeated imports safely reuse identical assets.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(m_ids[source.Path].ToString("N") + file.Path + file.Sha256));
        var assetId = new Guid(digest.AsSpan(0, 16));
        assets.TryAdd(assetId, file);
        return "/api/attachments/" + assetId.ToString("D") + (image && ImportPlanner.IsSupportedImage(file) ? "/content" : "/download");
    }

    private LinkResolution ToPage(string path, string? fragment)
    {
        var id = m_ids[path];
        if (fragment is null)
        {
            return new LinkResolution(PageHref(id));
        }
        var (heading, finding) = FindHeading(path, fragment);
        return new LinkResolution(PageHref(id, heading), finding, WikiImportFinding.Degraded);
    }

    private LinkResolution Url(WikiSourcePage source, string input, bool image, Dictionary<Guid, WikiSourceFile> assets)
    {
        if (input.Length > 4096 || input.Any(char.IsControl) || input.StartsWith("//", StringComparison.Ordinal))
        {
            return LinkResolution.None;
        }
        var value = input;
        var fromSourceWiki = false;
        if (!value.StartsWith('/') && Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            if (m_options.SourceBaseUrl is not { Length: > 0 } baseUrl || !Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var origin)
                || absolute.Scheme != origin.Scheme || absolute.Authority != origin.Authority || !absolute.AbsolutePath.StartsWith(origin.AbsolutePath, StringComparison.Ordinal))
            {
                return LinkResolution.None;
            }
            value = "/" + absolute.AbsolutePath[origin.AbsolutePath.Length..] + absolute.Fragment;
            fromSourceWiki = true;
        }
        var unresolved = fromSourceWiki ? new LinkResolution(null, "source-link-unresolved") : LinkResolution.None;
        var hash = value.IndexOf('#');
        var anchor = hash < 0 ? null : value[(hash + 1)..];
        if (hash >= 0)
        {
            value = value[..hash];
        }
        if (value.Contains('?'))
        {
            return unresolved;
        }
        try
        {
            value = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return unresolved;
        }
        if (value.Contains('\\') || value.Contains(':'))
        {
            return unresolved;
        }
        var parts = (value.StartsWith('/') ? value.TrimStart('/') : ImportPlanner.ParentPath(source.Path) + "/" + value).Split('/');
        var normalized = new List<string>();
        foreach (var part in parts)
        {
            if (part is "" or ".")
            {
                continue;
            }
            if (part == "..")
            {
                if (normalized.Count == 0)
                {
                    return unresolved;
                }
                normalized.RemoveAt(normalized.Count - 1);
            }
            else
            {
                normalized.Add(part);
            }
        }
        var path = value.Length == 0 ? source.Path : string.Join('/', normalized);
        // Of two entries differing only in letter case, the exact spelling reaches the renamed one.
        if (m_archive.Renamed.TryGetValue(path, out var exact))
        {
            path = exact;
        }
        if (!image)
        {
            var page = m_ids.ContainsKey(path) ? path : m_ids.ContainsKey(path + ".md") ? path + ".md" : null;
            if (page is not null)
            {
                return ToPage(page, anchor);
            }
        }
        if (m_archive.Files.TryGetValue(path, out var file) && !file.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return new LinkResolution(Attachment(source, file, image, assets));
        }
        // GitHub wikis link pages by name ("Release-Process") regardless of folder.
        var extension = Path.GetExtension(value);
        if (!image && !value.StartsWith('/') && (extension.Length == 0 || extension.Equals(".md", StringComparison.OrdinalIgnoreCase)) && FindPage(source, value) is { } named)
        {
            return Ambiguous(ToPage(named.Page.Path, anchor), named.Ambiguous);
        }
        return unresolved;
    }

    private static LinkResolution Ambiguous(LinkResolution resolution, bool ambiguous)
    {
        return ambiguous && resolution.Finding is null ? resolution with { Finding = "wiki-link-ambiguous", Impact = WikiImportFinding.Degraded } : resolution;
    }

    private LinkResolution Wiki(WikiSourcePage source, string target, string? alias, bool embed, Dictionary<Guid, WikiSourceFile> assets)
    {
        var hash = target.IndexOf('#');
        var name = hash < 0 ? target : target[..hash];
        var fragment = hash < 0 ? null : target[(hash + 1)..];
        var extension = Path.GetExtension(name);
        if (extension.Length > 1 && !extension.Equals(".md", StringComparison.OrdinalIgnoreCase) && FindFile(source, name) is { } found)
        {
            var href = Attachment(source, found.File, embed, assets);
            var label = alias is not null && !IsSize(alias) ? alias : Path.GetFileName(found.File.Path);
            return new LinkResolution(href, found.Ambiguous ? "embed-ambiguous" : null, WikiImportFinding.Degraded, label);
        }
        if (name.Trim().Length == 0)
        {
            return ToPage(source.Path, fragment) with { Label = alias ?? fragment?.Trim() };
        }
        var page = FindPage(source, name);
        var displayName = name.Trim();
        var ambiguous = page?.Ambiguous == true;
        if (alias is not null && !embed)
        {
            // GitHub writes [[Text|Page#heading]], Obsidian [[Page#heading|Text]]: take the reading whose target matches better.
            var aliasHash = alias.IndexOf('#');
            var aliasName = aliasHash < 0 ? alias : alias[..aliasHash];
            var swapped = aliasName.Trim().Length == 0 ? null : FindPage(source, aliasName);
            // Both readings name existing, different pages: the choice may be wrong.
            ambiguous |= page is not null && swapped is not null && !ReferenceEquals(page.Value.Page, swapped.Value.Page);
            if (swapped is { } other && other.Quality > (page?.Quality ?? 0))
            {
                return Ambiguous(ToPage(other.Page.Path, aliasHash < 0 ? null : alias[(aliasHash + 1)..]) with { Label = target.Trim() }, ambiguous || other.Ambiguous);
            }
        }
        if (page is null)
        {
            // The editor's paste import names the same unresolved constructs "embed" and "wiki-link".
            return new LinkResolution(null, embed ? "embed" : "wiki-link", WikiImportFinding.Unresolved, alias ?? Display(displayName, fragment));
        }
        var result = Ambiguous(ToPage(page.Value.Page.Path, fragment) with { Label = alias ?? Display(displayName, fragment) }, ambiguous);
        return embed && result.Finding is null ? result with { Finding = "embed-page", Impact = WikiImportFinding.Degraded } : result;
    }

    private static string Display(string name, string? fragment)
    {
        var heading = fragment?.Trim().TrimStart('^');
        return string.IsNullOrEmpty(heading) ? name : name + " > " + heading;
    }

    private static bool IsSize(string alias)
    {
        return alias.All(c => char.IsAsciiDigit(c) || c == 'x');
    }
}
