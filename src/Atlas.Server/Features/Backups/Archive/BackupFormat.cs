using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.Projector.Domain;
using Atlas.Server.Features.SpaceCovers.Domain;
using Atlas.Markdown.Sync;

namespace Atlas.Server.Features.Backups.Archive;

internal sealed record BackupEntry(string Path, long Size, string Sha256);
internal sealed record BackupManifest(string Format, int Version, string Mode, DateTime CreatedAt, string ContentToken, List<BackupEntry> Files);
internal sealed record BackupWiki(string Name, List<Guid> Spaces, List<Guid> Pages, List<Guid> Attachments, Dictionary<Guid, string> Authors);
/// <summary>Version (format 8, 3.6-C): how the revision groups into a version of the history, written only for a revision that is not a
/// version of its own without a summary; a revision without it restores as such, measured against the revision before it.</summary>
internal sealed record BackupRevision(Guid Id, int Version, string Title, Guid AuthorId, DateTime ActiveFrom, DateTime? ActiveTo, string SourceMarkdown, List<Guid> Attachments,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BackupRevisionVersion? Group = null);
/// <summary>The PageRevision version columns; <paramref name="Changes"/> is the stored RevisionChanges JSON.</summary>
internal sealed record BackupRevisionVersion(Guid GroupId, DateTime StartedAt, int Checkpoints, bool IsExplicit, string? Label, Guid? BaseRevisionId, string? Changes);
/// <summary>OwnAccess (format 2, WP3 S5): the page had own access. Written only when true, so an archive without own-access pages
/// stays format 1 and readable by 1.0; item grants are never archived (like space grants and accounts), so a restored own-access
/// page stays restricted (space Admin and WikiAdmin) instead of silently opening to the whole space. ChangeSummaries (format 4): the
/// change summary of each publication that has one, keyed by its Id in Publications and written only when non-empty; the entity
/// does not serialize it (older readers reject unknown members), so an archive without summaries stays readable by 1.5.</summary>
internal sealed record BackupPage(Page Page, List<BackupRevision> Revisions, List<PagePublication> Publications,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool OwnAccess = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsTemplate = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<Guid, string>? ChangeSummaries = null);
/// <summary>A space in the trash (format 7): when, and the batch its pages trashed with it share. Who is not archived (accounts
/// never are): the restorer becomes the one who trashed it.</summary>
internal sealed record BackupSpaceTrash(DateTime TrashedAt, Guid TrashBatchId);
internal sealed record BackupAttachment(Attachment Attachment, string File);
/// <summary>The image of a space in spaces/&lt;id&gt;/cover.json, written only when a person set it. Format 5, an upload:
/// <c>{ file, mimeType, size, sha256, tinted }</c> with its bytes in <see cref="File"/> next to it
/// (<see cref="BackupFormat.CoverPath"/>). Format 6, a chosen drawing or no image by choice: <c>{ source, catalogKey }</c> without
/// a file. One record serves both: a null member is not written, so an upload stays byte for byte what format 5 wrote. The
/// defaults make every member optional for the reader, which therefore checks the shape itself.</summary>
internal sealed record BackupSpaceCover(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? File = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MimeType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Size = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Tinted = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Source = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CatalogKey = null);

internal static class BackupFormat
{
    /// <summary>1: 1.0 archives and archives without own-access pages. 2: some page carries OwnAccess. 3: some page is a template.
    /// 4: some publication carries a change summary (BackupPage.ChangeSummaries, keyed by the id of the publication in page.json).
    /// 5: some space has an uploaded image (BackupSpaceCover). 6: some space has a chosen drawing or no image by choice
    /// (BackupSpaceCover.Source).</summary>
    internal const int Version = 1;
    internal const int ItemAccessVersion = 2;
    internal const int TemplateVersion = 3;
    internal const int ChangeSummaryVersion = 4;
    internal const int CoverVersion = 5;
    internal const int CoverChoiceVersion = 6;
    /// <summary>7: some space is in the trash (BackupSpaceTrash in spaces/&lt;id&gt;/trash.json, full archives only).</summary>
    internal const int SpaceTrashVersion = 7;
    /// <summary>8: some revision is grouped into a version, named or summarized (BackupRevision.Group, Atlas 3.6).</summary>
    internal const int RevisionVersionsVersion = 8;

    internal const long MaximumZipBytes = 1024L * 1024 * 1024;
    internal const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024;
    internal const long MaximumFileBytes = 32L * 1024 * 1024;
    internal const long MaximumMetadataBytes = 256L * 1024 * 1024;
    internal const int MaximumEntries = 100000;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Page content is written through these options (RemapJson): as deep as a stored document may be.
        MaxDepth = MarkdownDocumentValidator.JsonMaxDepth,
        WriteIndented = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static void RequireMode(string mode, bool import = false)
    {
        if (import ? mode is not ("add" or "restore") : mode is not ("full" or "latest"))
        {
            throw new InvalidDataException("Neplatný režim zálohy.");
        }
    }

    internal static string BinaryPath(Attachment attachment)
    {
        var name = AttachmentFiles.SafeName(attachment.Name);
        name = new string(name.Select(c => char.IsControl(c) || "<>:\"/\\|?*#%".Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        if (string.IsNullOrEmpty(name) || name.Equals("attachment.json", StringComparison.OrdinalIgnoreCase))
        {
            name = "file-" + name;
        }
        return $"attachments/{attachment.Id:D}/{name}";
    }

    internal static string CoverMetadataPath(Guid spaceId) => $"spaces/{spaceId:D}/cover.json";
    internal static string TrashMetadataPath(Guid spaceId) => $"spaces/{spaceId:D}/trash.json";

    /// <summary>spaces/&lt;id&gt;/cover.png, .jpg or .webp; null for a type a card image cannot have.</summary>
    internal static string? CoverPath(Guid spaceId, string mimeType) =>
        SpaceCoverImage.Extension(mimeType) is { } extension ? $"spaces/{spaceId:D}/cover{extension}" : null;

    internal static void RequirePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.StartsWith('/') || path.Contains('\\') ||
            path.Any(c => char.IsControl(c) || c == ':') || path.Split('/').Any(p => p.Length == 0 || p is "." or "..") ||
            path.Split('/').Any(p => p.EndsWith('.') || p.EndsWith(' ')))
        {
            throw new InvalidDataException("ZIP obsahuje nebezpečnou cestu.");
        }
    }

    internal static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            BufferSize = 65536, Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return new FileStream(path, options);
    }

    internal static async Task<BackupEntry> CopyAsync(Stream input, Stream output, string path, long limit, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long size = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }
            size += read;
            if (size > limit)
            {
                throw new InvalidDataException("Záloha překračuje povolenou velikost.");
            }
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return new BackupEntry(path, size, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    internal static string Suffix(BackupManifest manifest) => "-restore-" + manifest.CreatedAt.ToUniversalTime().ToString("yyyyMMddHHmmss") + "-" + manifest.ContentToken[..8];

    internal static string WithSuffix(string name, string suffix) => name[..Math.Min(name.Length, 200 - suffix.Length)] + suffix;

    private const string PageUrlPattern = """(?:https?://[^\s<>"#)]*)?(?<route>(?:/#|#)?/pages/)(?<id>[0-9a-fA-F-]{36})(?:\?heading=(?<heading>[^\s<>"#)&]+))?""";
    private const string AttachmentUrlPattern = """(?:https?://[^\s<>"#)]*)?(?<route>/api/attachments/)(?<id>[0-9a-fA-F-]{36})(?<action>/(?:content|download))""";

    internal static string Remap(string content, IReadOnlyDictionary<Guid, Guid> ids)
    {
        // Rewrite reference URLs and structured identifier fields only; prose UUIDs remain untouched.
        // Unchanged identifiers retain their original spelling and native document formatting.
        var result = Replace(content, PageUrlPattern, match =>
        {
            if (!TryRemap(match.Groups["id"].Value, ids, out var target))
                return match.Value;
            var heading = match.Groups["heading"].Success ? "?heading=" + match.Groups["heading"].Value : "";
            return match.Groups["route"].Value + target + heading;
        });
        result = Replace(result, AttachmentUrlPattern, match => TryRemap(match.Groups["id"].Value, ids, out var target)
            ? match.Groups["route"].Value + target + match.Groups["action"].Value : match.Value);
        return Replace(result, """"(?<prefix>"(?:pageId|attachmentId)"\s*:\s*")(?<id>[0-9a-fA-F-]{36})(?<suffix>")"""",
            match => TryRemap(match.Groups["id"].Value, ids, out var target)
                ? match.Groups["prefix"].Value + target + match.Groups["suffix"].Value : match.Value);
    }

    internal static string RemapJson(string content, IReadOnlyDictionary<Guid, Guid> ids)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(content, documentOptions: MarkdownDocumentValidator.JsonOptions);
        var changed = Visit(root);
        // Stored content, not a backup file: compact and raw UTF-8 like every other write of rich JSON (D-M2).
        return changed ? root!.ToJsonString(Atlas.Server.Platform.Content.RichDocument.JsonWrite) : content;

        bool Visit(System.Text.Json.Nodes.JsonNode? node)
        {
            var any = false;
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                // A page reference stores its target as attrs.id next to a url; both must follow the copy.
                if (obj["type"] is System.Text.Json.Nodes.JsonValue type && type.TryGetValue<string>(out var kind) && kind == "pageReference"
                    && obj["attrs"] is System.Text.Json.Nodes.JsonObject attrs && attrs["id"] is System.Text.Json.Nodes.JsonValue reference
                    && reference.TryGetValue<string>(out var referenced) && TryRemap(referenced, ids, out var copied))
                {
                    attrs["id"] = copied;
                    any = true;
                }
                // What a text node says stays as written, an address of a page in it too (GAP2-8).
                var prose = obj["type"] is System.Text.Json.Nodes.JsonValue nodeType && nodeType.TryGetValue<string>(out var name) && name == "text";
                foreach (var (key, value) in obj.ToList())
                {
                    if (value is System.Text.Json.Nodes.JsonValue scalar && scalar.TryGetValue<string>(out var text))
                    {
                        var replacement = key switch
                        {
                            "pageId" or "attachmentId" when TryRemap(text, ids, out var target) => target,
                            "requires" => RemapRequires(text, ids),
                            "text" when prose => text,
                            _ => Remap(text, ids)
                        };
                        if (replacement != text)
                        {
                            obj[key] = replacement;
                            any = true;
                        }
                    }
                    else
                        any |= Visit(value);
                }
            }
            else if (node is System.Text.Json.Nodes.JsonArray array)
            {
                foreach (var child in array)
                    any |= Visit(child);
            }
            return any;
        }
    }

    /// <summary>A requirement's dependencies on other pages' blocks (<c>key@pageId</c>) follow the copied pages (GAP2-5).</summary>
    private static string RemapRequires(string requires, IReadOnlyDictionary<Guid, Guid> ids)
    {
        var parts = RequirementAttributes.ParseRequires(requires);
        var remapped = parts.Select(x => RequirementAttributes.CrossPage(x) is { } other && TryRemap(other.PageId.ToString("D"), ids, out var target)
            ? other.Key + "@" + target : x).ToList();
        return remapped.SequenceEqual(parts) ? requires : string.Join(",", remapped);
    }

    private static bool TryRemap(string value, IReadOnlyDictionary<Guid, Guid> ids, out string target)
    {
        target = value;
        if (!Guid.TryParse(value, out var original) || !ids.TryGetValue(original, out var replacement) || original == replacement)
            return false;
        target = replacement.ToString("D");
        return true;
    }

    private static string Replace(string content, string pattern, System.Text.RegularExpressions.MatchEvaluator replacement) =>
        System.Text.RegularExpressions.Regex.Replace(content, pattern, replacement,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    internal static string PortableMarkdown(string markdown, string directory, IReadOnlySet<Guid> pages, IReadOnlyDictionary<Guid, string> files)
        => PortableMarkdown(markdown, directory, id => pages.Contains(id) ? $"pages/{id:D}/content.md" : null, files);

    /// <summary>Rewrites page and attachment URLs into paths relative to <paramref name="directory"/>. <paramref name="pageFile"/>
    /// gives a linked page's file inside the archive or repository; a page it does not know keeps its original link.</summary>
    internal static string PortableMarkdown(string markdown, string directory, Func<Guid, string?> pageFile, IReadOnlyDictionary<Guid, string> files)
    {
        var depth = directory.Count(c => c == '/') + 1;
        var root = string.Concat(Enumerable.Repeat("../", depth));
        var result = Replace(markdown, PageUrlPattern, match =>
        {
            if (!Guid.TryParse(match.Groups["id"].Value, out var id) || pageFile(id) is not { } file)
                return match.Value;
            var heading = match.Groups["heading"].Success ? "#" + match.Groups["heading"].Value : "";
            return root + Escape(file) + heading;
        });
        return Replace(result, AttachmentUrlPattern,
            match => Guid.TryParse(match.Groups["id"].Value, out var id) && files.TryGetValue(id, out var path)
                ? root + Escape(path) : match.Value);

        static string Escape(string path) => string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
    }
}
