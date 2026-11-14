using System.Security.Cryptography;
using System.Text;
using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.ContentImport.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ContentImport.Domain;

public static class ImportPlanner
{
    /// <summary>
    /// The plan of every convertible page. <c>Archive</c> is <paramref name="archive"/> with the pages that
    /// could not be converted added to its skipped files, so the report lists them as unsupported.
    /// </summary>
    public static async Task<(List<ImportPlan> Plans, WikiSourceArchive Archive)> PlanAsync(ISession session, WikiSourceArchive archive, Guid sourceId, WikiImportOptions options,
        WikiImportReport? reviewed, CancellationToken ct)
    {
        var sources = Sources(archive);
        var sourcePaths = sources.Select(p => p.Path.ToLowerInvariant()).ToArray();
        var maps = sourcePaths.Length == 0 ? new List<WikiImportPage>() : await session.QueryOver<WikiImportPage>()
            .Where(x => x.SourceId == sourceId)
            .And(Restrictions.In(Projections.SqlFunction("lower", NHibernateUtil.String, Projections.Property("SourcePath")), sourcePaths))
            .ListAsync(ct);
        var ids = sources.ToDictionary(p => p.Path, p => maps.FirstOrDefault(m => m.SourcePath.Equals(p.Path, StringComparison.OrdinalIgnoreCase))?.PageId
            ?? reviewed?.Items.FirstOrDefault(i => i.Path == p.Path)?.PageId ?? Guid.NewGuid(), StringComparer.OrdinalIgnoreCase);
        var (converted, unsupported) = Convert(archive, sources, ids, options);
        var plans = new List<ImportPlan>();
        foreach (var (source, document, assets) in converted)
        {
            var fingerprint = System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Sha256 + document.ContentJson + document.Markdown + string.Join("", assets.OrderBy(x => x.Value.Path, StringComparer.Ordinal).Select(x => x.Value.Path + x.Value.Sha256)))));
            var map = maps.FirstOrDefault(m => m.SourcePath.Equals(source.Path, StringComparison.OrdinalIgnoreCase));
            var page = map is null ? null : await session.GetAsync<Page>(map.PageId, ct);
            var checkpoint = map is null ? null : await session.QueryOver<WikiImportCheckpoint>().Where(x => x.ImportPageId == map.Id && x.ActiveTo == null).SingleOrDefaultAsync(ct);
            var imported = checkpoint is null ? null : await session.GetAsync<PageRevision>(checkpoint.RevisionId, ct);
            var action = map is null ? "create" : checkpoint?.SourceSha256 == fingerprint ? "skip"
                : options.UpdateExisting && page is { IsTrashed: false, PublishedRevisionId: null } && page.Version == imported?.Version ? "update" : "conflict";
            var liveState = page is null ? null : await session.GetAsync<PageCollaboration>(page.Id, ct);
            if (action == "update" && liveState is { Sequence: > 0 })
            {
                action = "conflict";
            }
            var warnings = source.Warnings.ToList();
            if (map is not null && map.SourcePath != source.Path)
            {
                action = "conflict";
                warnings.Add("Velikost písmen zdrojové cesty se změnila; automatické přejmenování není bezpečné.");
            }
            if (action == "conflict")
            {
                warnings.Add("Zdroj se změnil nebo byl cíl upraven. Uživatelské změny se nikdy automaticky nepřepisují.");
            }
            var old = reviewed?.Items.FirstOrDefault(x => x.Path == source.Path);
            if (old is not null && (old.Sha256 != fingerprint || old.PageId != ids[source.Path] || action != "skip" && (old.Action != action || old.ExpectedVersion != page?.Version)))
            {
                action = "conflict";
            }
            plans.Add(new ImportPlan(source, document, assets, new WikiImportItem(source.Path, source.SourceFilePath.Length > 0 ? "page" : "folder", action, source.Title,
                ids[source.Path], page?.Version, fingerprint, warnings, document.Findings, source.Tags)));
        }
        if (unsupported.Count == 0)
        {
            return (plans, archive);
        }
        var skipped = new Dictionary<string, string>(archive.Skipped, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, reason) in unsupported)
        {
            skipped[path] = reason;
        }
        return (plans, archive with { Skipped = skipped });
    }

    /// <summary>The archive's pages plus an organisational page for every folder without one.</summary>
    public static List<WikiSourcePage> Sources(WikiSourceArchive archive)
    {
        var sources = archive.Pages.ToList();
        var allPaths = sources.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var original in archive.Pages)
        {
            var parent = ParentPath(original.Path);
            while (parent.Length > 0)
            {
                if (allPaths.Add(parent + ".md"))
                {
                    sources.Add(new WikiSourcePage(parent + ".md", parent.Split('/')[^1], "", "folder:" + parent,
                        ["Organizační stránka vytvořená pro složku exportu bez vlastní stránky."]));
                }
                parent = ParentPath(parent);
            }
        }
        if (sources.Count > 500)
        {
            throw new ApiException(413, "Import včetně organizačních složek smí mít nejvýše 500 stránek.");
        }
        return sources;
    }

    /// <summary>
    /// Converts every page with links resolved across the whole import. Heading outlines are read
    /// first, so a link may target a heading of a page that is converted later. A page the editor could
    /// not hold (nesting deeper than 32 levels, a document over the size limits) is left out and returned
    /// in <c>Unsupported</c> with the reason, keyed by its archive file, instead of failing the import;
    /// the remaining pages are then converted again so no link points at the missing page.
    /// </summary>
    public static (List<(WikiSourcePage Source, ImportedDocument Document, Dictionary<Guid, WikiSourceFile> Assets)> Pages, IReadOnlyDictionary<string, string> Unsupported) Convert(
        WikiSourceArchive archive, IReadOnlyList<WikiSourcePage> sources, IReadOnlyDictionary<string, Guid> ids, WikiImportOptions options)
    {
        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var outlines = new Dictionary<string, IReadOnlyList<(string Text, string Id)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            try
            {
                outlines[source.Path] = WikiMarkdownConverter.Outline(source);
            }
            catch (ApiException error) when (error.Status == 400)
            {
                failed[source.Path] = error.Message;
            }
        }
        while (true)
        {
            var remaining = sources.Where(x => !failed.ContainsKey(x.Path)).ToList();
            var remainingIds = ids.Where(x => !failed.ContainsKey(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            var resolver = new ImportLinkResolver(archive, remaining, remainingIds, options, outlines);
            var result = new List<(WikiSourcePage, ImportedDocument, Dictionary<Guid, WikiSourceFile>)>();
            var failures = failed.Count;
            long totalAssets = 0;
            long convertedCharacters = 0;
            foreach (var source in remaining.OrderBy(p => p.Path, StringComparer.Ordinal))
            {
                var assets = new Dictionary<Guid, WikiSourceFile>();
                ImportedDocument document;
                try
                {
                    document = WikiMarkdownConverter.Convert(source, resolver.For(source, assets));
                }
                catch (ApiException error) when (error.Status == 400)
                {
                    failed[source.Path] = error.Message;
                    continue;
                }
                convertedCharacters += document.ContentJson.Length;
                if (convertedCharacters > 16L * 1024 * 1024)
                {
                    throw new ApiException(413, "Převedený obsah importu je příliš velký; rozdělte export na menší části.");
                }
                totalAssets += assets.Values.Sum(x => x.Bytes.LongLength);
                if (totalAssets > WikiJsArchive.MaximumExpandedBytes)
                {
                    throw new ApiException(413, "Kopie příloh pro jednotlivé stránky překračují 256 MiB.");
                }
                result.Add((source, document, assets));
            }
            if (failed.Count == failures)
            {
                var unsupported = sources.Where(x => failed.ContainsKey(x.Path)).ToDictionary(x => x.SourceFilePath,
                    x => "Stránku nelze převést: " + failed[x.Path] + " Originál zůstává v ZIP.", StringComparer.OrdinalIgnoreCase);
                return (result, unsupported);
            }
        }
    }

    public static string RemoveExtension(string path)
    {
        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;
    }

    public static string ParentPath(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "" : path[..index];
    }

    public static bool IsSupportedImage(WikiSourceFile file)
    {
        if (file.Bytes.LongLength > AttachmentFiles.MaximumImageBytes)
        {
            return false;
        }
        try
        {
            AttachmentFiles.DetectImage(file.Bytes);
            return true;
        }
        catch (ApiException)
        {
            return false;
        }
    }
}
