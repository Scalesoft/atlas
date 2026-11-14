using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.Backups.Archive;
using Atlas.Markdown.Sync;

namespace Atlas.Server.Features.Backups.Git;

public sealed record GitSpace(Guid Id, string Name, Guid? ChangedBy, DateTime? ChangedAt);

/// <summary>One page as the sync sees it: placement, the current revision and who last organized or published it.</summary>
public sealed record GitPage(Guid Id, Guid SpaceId, Guid? ParentId, int Version, bool IsTrashed, string Title, Guid CurrentRevisionId,
    int? PublishedVersion, Guid? PublisherId, DateTime? PublishedAt, Guid? OrganizerId, DateTime? OrganizedAt, Guid? TrashedById, DateTime? TrashedAt,
    Guid? TrashBatchId = null, bool IsPurged = false);

/// <summary>
/// The repository layout: readable, lowercase ASCII paths from space and page names (<c>space/parent/page.md</c>, children in
/// <c>space/parent/page/</c>, attachments in <c>space/parent/page.assets/</c>) and the lossless native document under
/// <c>.atlas/pages/&lt;id&gt;.json</c>. Lowercase names keep the tree unique on case-insensitive file systems.
/// </summary>
public static class GitBackupLayout
{
    public const string MarkerPath = ".atlas/repository.json";
    public const string ReadmePath = "README.md";
    private const int MaximumSlug = 80;
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // A page file embeds the stored content one level down; MarkdownDocumentValidator.JsonMaxDepth leaves room for it.
        MaxDepth = MarkdownDocumentValidator.JsonMaxDepth,
        WriteIndented = true,
        // The files are data in a repository, never HTML: keep Czech text readable in diffs.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string NativePath(Guid page) => $".atlas/pages/{page:D}.json";

    /// <summary>Keeps git from converting line endings of the format v7 files on any clone, which would break their checksums.</summary>
    public const string AttributesPath = ".atlas/.gitattributes";
    public const string Attributes = "* -text\n";

    /// <summary>The restorable part of the tree (format v7), which the installation snapshot writes; the page sync leaves it alone.</summary>
    public static bool IsInstallationData(string path)
        => path is Platform.Backup.BackupManifest.Path or AttributesPath || path.StartsWith(".atlas/modules/", StringComparison.Ordinal)
           || path.StartsWith(".atlas/blobs/", StringComparison.Ordinal) || path.StartsWith(".atlas/secure/", StringComparison.Ordinal);

    public static string MarkdownPath(string path) => path + ".md";
    public static string AssetsPath(string path) => path + ".assets";

    public static string Slug(string text, string fallback = "stranka")
    {
        var builder = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
                builder.Append(lower);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }
        var slug = builder.ToString().Trim('-');
        if (slug.Length > MaximumSlug)
            slug = slug[..MaximumSlug].TrimEnd('-');
        if (slug.Length == 0)
            slug = fallback;
        return Reserved.Contains(slug) ? slug + "-" : slug;
    }

    private static string Short(Guid id) => id.ToString("N")[..8];

    /// <summary>
    /// Paths (without extension) of the pages in the tree. A trashed page has none. Among siblings with the same name the page
    /// that already holds the path in the repository keeps it; the others get a short id suffix.
    /// </summary>
    public static Dictionary<Guid, string> Paths(IReadOnlyCollection<GitSpace> spaces, IReadOnlyCollection<GitPage> pages,
        IReadOnlyDictionary<Guid, GitBackupPage> state)
    {
        var live = pages.Where(x => !x.IsTrashed).ToDictionary(x => x.Id);
        var spaceFolders = new Dictionary<Guid, string>();
        var heldSpaces = state.Values.Where(x => x.Path is not null).Join(pages, x => x.PageId, x => x.Id, (s, p) => (p.SpaceId, Folder: s.Path!.Split('/')[0]))
            .ToHashSet();
        foreach (var group in spaces.GroupBy(x => Slug(x.Name, "prostor")))
        {
            var ordered = group.OrderBy(x => heldSpaces.Contains((x.Id, group.Key)) ? 0 : 1).ThenBy(x => x.Id).ToList();
            for (var i = 0; i < ordered.Count; i++)
                spaceFolders[ordered[i].Id] = i == 0 ? group.Key : group.Key + "-" + Short(ordered[i].Id);
        }
        var children = live.Values.ToLookup(x => x.ParentId is { } parent && live.TryGetValue(parent, out var p) && p.SpaceId == x.SpaceId
            ? (x.SpaceId, (Guid?)parent) : (x.SpaceId, null));
        var paths = new Dictionary<Guid, string>();
        var queue = new Queue<(Guid Space, Guid? Parent, string Folder)>(spaceFolders.Select(x => (x.Key, (Guid?)null, x.Value)));
        while (queue.Count > 0)
        {
            var (space, parent, folder) = queue.Dequeue();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in children[(space, parent)].GroupBy(x => Slug(x.Title)).OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                // Only the name counts: after a moved parent or a renamed space the same page keeps it.
                var ordered = group.OrderBy(x => state.TryGetValue(x.Id, out var s) && s.Path?[(s.Path.LastIndexOf('/') + 1)..] == group.Key ? 0 : 1)
                    .ThenBy(x => x.Id).ToList();
                for (var i = 0; i < ordered.Count; i++)
                {
                    var name = i == 0 && taken.Add(group.Key) ? group.Key : group.Key + "-" + Short(ordered[i].Id);
                    if (i > 0 && !taken.Add(name))
                        name = group.Key + "-" + ordered[i].Id.ToString("N");
                    var path = folder + "/" + name;
                    paths[ordered[i].Id] = path;
                    queue.Enqueue((space, ordered[i].Id, path));
                }
            }
        }
        return paths;
    }

    /// <summary>Where a trashed page that never had a repository path is written before its deletion commit.</summary>
    public static string DetachedPath(GitPage page, IReadOnlyDictionary<Guid, string> paths, IReadOnlyCollection<GitSpace> spaces)
    {
        var folder = page.ParentId is { } parent && paths.TryGetValue(parent, out var parentPath) ? parentPath
            : Slug(spaces.FirstOrDefault(x => x.Id == page.SpaceId)?.Name ?? "", "prostor");
        return folder + "/" + Slug(page.Title) + "-" + Short(page.Id);
    }

    /// <summary>File names of a revision's attachments in the page's .assets folder, unique regardless of letter case.</summary>
    public static Dictionary<Guid, string> AssetNames(IEnumerable<Attachment> attachments)
    {
        var names = new Dictionary<Guid, string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attachment in attachments.OrderBy(x => x.ActiveFrom).ThenBy(x => x.Id))
        {
            var name = BackupFormat.BinaryPath(attachment).Split('/')[^1];
            // ".git", ".gitignore" or ".gitattributes" would steer git itself.
            if (name.StartsWith('.'))
                name = "_" + name;
            if (name.Length > 120)
            {
                var extension = Path.GetExtension(name);
                name = name[..(120 - Math.Min(extension.Length, 20))] + extension[..Math.Min(extension.Length, 20)];
            }
            if (!taken.Add(name))
            {
                name = Path.GetFileNameWithoutExtension(name) + "-" + Short(attachment.Id) + Path.GetExtension(name);
                taken.Add(name);
            }
            names[attachment.Id] = name;
        }
        return names;
    }

    public static string Markdown(Guid pageId, string title, int version, int? published, DateTime updated, string author, string body)
    {
        var builder = new StringBuilder("---\n");
        builder.Append("title: ").Append(Quote(title)).Append('\n');
        builder.Append("id: ").Append(pageId.ToString("D")).Append('\n');
        builder.Append("version: ").Append(version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (published is { } publishedVersion)
            builder.Append("published: ").Append(publishedVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("updated: ").Append(updated.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("author: ").Append(Quote(author)).Append('\n');
        builder.Append("---\n\n").Append(body.Replace("\r\n", "\n"));
        if (builder[^1] != '\n')
            builder.Append('\n');
        return builder.ToString();
    }

    public static string Native(Guid pageId, Guid? parentId, string title, int version, string contentJson, IList<JsonNode?> attachments)
    {
        JsonNode? content;
        try { content = JsonNode.Parse(contentJson, documentOptions: MarkdownDocumentValidator.JsonOptions); }
        catch (JsonException) { content = JsonValue.Create(contentJson); }
        var document = new JsonObject
        {
            ["format"] = "atlas-git-backup-page",
            ["version"] = 1,
            ["id"] = pageId.ToString("D"),
            ["parentId"] = parentId?.ToString("D"),
            ["title"] = title,
            ["revision"] = version,
            ["attachments"] = new JsonArray([.. attachments]),
            ["content"] = content
        };
        return document.ToJsonString(Json) + "\n";
    }

    public static string Marker(Guid repositoryId) => new JsonObject
    {
        ["format"] = "atlas-git-backup",
        ["version"] = 1,
        ["repositoryId"] = repositoryId.ToString("D")
    }.ToJsonString(Json) + "\n";

    /// <summary>Attachment file name → attachment id and SHA-256 as recorded in a native page document.</summary>
    public static Dictionary<string, (string Id, string Sha256)> NativeAttachments(string? text)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        try
        {
            if (text is not null && JsonNode.Parse(text, documentOptions: MarkdownDocumentValidator.JsonOptions)?["attachments"] is JsonArray items)
                foreach (var item in items)
                    if (item?["file"]?.GetValue<string>() is { } file && item["id"]?.GetValue<string>() is { } id && item["sha256"]?.GetValue<string>() is { } sha)
                        result[file] = (id, sha);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
        }
        return result;
    }

    /// <summary>The revision number a native page document holds, if it is one.</summary>
    public static int? NativeRevision(string? text)
    {
        try
        {
            return text is not null && JsonNode.Parse(text, documentOptions: MarkdownDocumentValidator.JsonOptions) is JsonObject node && node["format"]?.GetValue<string>() == "atlas-git-backup-page"
                ? node["revision"]?.GetValue<int>() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public static Guid? ReadMarker(string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            return node?["format"]?.GetValue<string>() == "atlas-git-backup" && Guid.TryParse(node["repositoryId"]?.GetValue<string>(), out var id)
                ? id : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public const string Readme = """
        # Atlas – průběžná záloha obsahu

        Tento repozitář zapisuje Atlas automaticky po každé uložené změně obsahu wiki. Neupravujte ho ručně.

        - `<prostor>/<stránka>.md` – aktuální verze stránky jako Markdown s hlavičkou (title, id, version, published, updated, author).
        - `<prostor>/<stránka>/` – podstránky, `<prostor>/<stránka>.assets/` – přílohy a obrázky aktuální verze.
        - `.atlas/pages/<id>.json` – nativní obsah editoru (bezztrátový) a identita stránky.
        - Historie změn je historie gitu: autorem commitu je uživatel, který změnu v Atlasu provedl.
        - `.atlas/manifest.json`, `.atlas/modules/`, `.atlas/blobs/`, `.atlas/secure/` – úplná záloha instalace (formát 7):
          všechny moduly bezztrátově, přílohy jednou podle obsahu, účty, granty a tajemství šifrované klíčem zálohy.

        Repozitář obsahuje veškerý obsah wiki včetně konceptů a stránek s omezeným přístupem. Účty, hesla, přístupová práva
        a tajemství jsou jen v šifrované části; bez klíče zálohy jdou obnovit pouze obsah a nastavení bez tajemství.
        Obnova: Správa wiki → Provoz → Zálohy → Obnova instalace. Markdown strom lze načíst i importem z Wiki.js.

        """;

    /// <summary>JSON strings are valid YAML double-quoted scalars.</summary>
    private static string Quote(string value) => JsonSerializer.Serialize(value, Json);
}
