using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Queries;
using Atlas.Server.Features.PersonalApi;

namespace Atlas.Server.Features.Mcp.Tools;

/// <summary>
/// Attachments over MCP (API/MCP spec §1): list, metadata, upload and download of a page's files and images, through the same
/// decisions as the web and REST (<c>AttachmentCatalog</c>, <c>AttachmentPageGuard</c>, the shared store) narrowed by G5.
/// <list type="bullet">
/// <item>Binary transport is bounded base64 in one JSON-RPC message each way, no session and no persistence beyond the attachment
/// itself. An upload takes at most <see cref="MaximumUploadBytes"/> decoded bytes of strict padded base64, refused by length before
/// anything is allocated; with the JSON-RPC envelope it fits <c>McpModule.MaximumRequestBytes</c>, the one limit of every MCP request.
/// Larger files go through REST multipart.</item>
/// <item>A download returns the original bytes (never a preview) as an embedded blob resource in chunks of at most
/// <see cref="MaximumChunkBytes"/>; every chunk names the whole file's size and SHA-256, so a client can fetch the rest by offset
/// statelessly and verify what it saved.</item>
/// <item>Upload runs the transaction sequence of the REST upload: the preflight (guard and G5), decoding and the disk write outside
/// any transaction, then <see cref="StoreMcpAttachment"/> under the page locks. It never changes the page body, creates no revision
/// and publishes nothing.</item>
/// </list>
/// Neither arguments nor bytes are logged. Not a Wolverine handler: the class name does not end with "Handler".
/// </summary>
[McpServerToolType]
public sealed class McpAttachmentTools
{
    /// <summary>
    /// The decoded maximum of one MCP upload, for both kinds: 2 MiB, so that its base64 with the envelope fits the 3 000 000 B of every
    /// MCP request and no call makes the server parse more (coordinator's decision 2026-10-12, from verification: Claude Code reliably
    /// sends only 1–2 KB of base64, and a program uploads up to 10 MiB images and 25 MiB files through REST multipart).
    /// </summary>
    public const long MaximumUploadBytes = 2 * 1024 * 1024;

    /// <summary>Standard padded base64 of <see cref="MaximumUploadBytes"/>: 2 796 204 characters.</summary>
    public const int MaximumUploadBase64Length = (int)(4 * ((MaximumUploadBytes + 2) / 3));

    /// <summary>Original bytes per download call (base64 about 5.6 million characters).</summary>
    public const int MaximumChunkBytes = 4 * 1024 * 1024;

    private const int ListPage = 100;
    private const string RestUpload = "POST /api/v1/pages/{pageId}/attachments (multipart/form-data, pole file a kind)";

    private static readonly SearchValues<char> Base64Alphabet = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/");

    private readonly IMessageBus m_bus;
    private readonly AttachmentFiles m_files;
    private readonly McpAddress m_address;
    private readonly ILogger<McpAttachmentTools> m_logger;

    public McpAttachmentTools(IMessageBus bus, AttachmentFiles files, McpAddress address, ILogger<McpAttachmentTools> logger)
    {
        m_bus = bus;
        m_files = files;
        m_address = address;
        m_logger = logger;
    }

    [McpServerTool(Name = "atlas_upload_attachment", Title = "Nahrát přílohu ke stránce", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [Description("Nahraje soubor nebo obrázek k existující stránce, kterou smíte upravovat. Obsah se předává jako standardní base64 s doplněním " +
        "(=), bez mezer a zalomení, nejvýše 2 MiB po dekódování (2 796 204 znaků); celý požadavek MCP smí mít nejvýše 3 000 000 B. kind \"image\" je vložitelný obrázek: jen PNG, JPEG, GIF " +
        "nebo WebP podle skutečného obsahu (SVG ne); kind \"file\" je příloha ke stažení. Větší soubory (obrázky do 10 MiB, přílohy do 25 MiB) nahrajte přes REST: " +
        RestUpload + ". Nahrání stránku nemění: text, verze ani publikace zůstanou, příloha je jen připravená k vložení. Každé volání vytvoří " +
        "novou přílohu; po nejisté odpovědi neopakujte naslepo, nejdřív ověřte atlas_list_attachments. Povinný sha256 (hex SHA-256 původních bajtů) " +
        "server porovná s dekódovaným obsahem a při neshodě nic neuloží (hash_mismatch): jazykové modely totiž při přepisu base64 delšího než " +
        "asi 3 KB nepozorovaně chybují, proto tímto nástrojem spolehlivě pošlete jen malé soubory (řádově 1–2 KB base64), větší přes REST. " +
        "Vrací attachmentId, url a downloadUrl.")]
    public Task<CallToolResult> UploadAttachmentAsync(
        [Description("pageId stránky, ke které příloha patří.")] string pageId,
        [Description("\"image\" (obrázek pro vložení do textu) nebo \"file\" (soubor ke stažení).")] string kind,
        [Description("Název souboru včetně přípony, např. schema-site.png (uloží se bez cesty).")] string fileName,
        [Description("Obsah souboru jako standardní base64 s doplněním, bez mezer a zalomení řádků.")] string contentBase64,
        [Description("SHA-256 původních bajtů souboru v hex zápisu (64 znaků, malá i velká písmena). Neshoda s obsahem = nic se neuloží.")] string sha256,
        CancellationToken ct = default)
        => RunAsync("atlas_upload_attachment", async () =>
        {
            var page = McpArguments.RequiredId(pageId, "pageId");
            var image = kind switch
            {
                "image" => true,
                "file" => false,
                _ => throw new ApiException(400, "kind musí být \"image\" nebo \"file\".", "invalid_argument")
            };
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 1_024)
            {
                return McpErrors.InvalidArgument("fileName je povinné, nejvýše 1 024 znaků.");
            }
            if (sha256 is not { Length: 64 } || !sha256.All(char.IsAsciiHexDigit))
            {
                return McpErrors.InvalidArgument("sha256 je povinné: 64 hex znaků SHA-256 původních bajtů souboru.");
            }
            // The length decides before anything is decoded or authorized: an oversized argument costs no allocation and no statement.
            var encoded = contentBase64 ?? "";
            if (encoded.Length > MaximumUploadBase64Length)
            {
                throw TooLarge(image);
            }
            await m_bus.InvokeAsync(new AuthorizeMcpAttachmentUpload(page), ct);
            var bytes = Decode(encoded, image);
            // Before any disk write or statement: a model that retyped long base64 may have corrupted it, and the signature check alone
            // would still accept a damaged PNG.
            if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ApiException(400, "Přenesený obsah neodpovídá zadanému SHA-256, nic se neuložilo. Větší soubory nahrajte přes REST: " + RestUpload + ".", "hash_mismatch");
            }
            if (image)
            {
                // The shared signature check (also repeated by the write) refuses SVG or anything else before the disk is touched.
                AttachmentFiles.DetectImage(bytes);
            }
            var id = Guid.NewGuid();
            (long Size, string Hash, string MimeType) written;
            await using (var stream = new MemoryStream(bytes, writable: false))
            {
                // The shared write removes its own partial file on any failure, a cancellation included.
                written = await m_files.WriteAsync(id, stream, image, ct, fileName);
            }
            // Once the bytes are on disk the short store runs to its end: a cancellation between the two transactions would strand the
            // blob, and the store either commits, removes the blob itself, or (an uncertain COMMIT) keeps it under the orphan policy.
            var stored = await m_bus.InvokeAsync<McpStoredAttachment>(
                new StoreMcpAttachment(id, page, fileName, written.Size, written.Hash, written.MimeType, image), CancellationToken.None);
            return Uploaded(stored, image, written.Hash);
        }, ct);

    [McpServerTool(Name = "atlas_list_attachments", Title = "Přílohy stránky", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Přílohy a obrázky stránky: attachmentId, název, typ, velikost a použití. pageId, cursor.")]
    [Description("Vypíše přílohy a obrázky stránky, které smíte otevřít: attachmentId, název, MIME typ, velikost, SHA-256, čas nahrání, odkazy " +
        "a zda je používá publikovaná verze (usedInPublished) a pracovní verze (usedInWorking, jen editorovi). Editor vidí i nahrané, dosud " +
        "nepoužité přílohy; čtenář jen přílohy publikované verze. Pokračování přes nextCursor. " + McpText.TrustNotice)]
    public Task<CallToolResult> ListAttachmentsAsync(
        [Description("pageId stránky.")] string pageId,
        [Description("nextCursor z předchozího výsledku.")] string? cursor = null,
        CancellationToken ct = default)
        => RunAsync("atlas_list_attachments", async () =>
        {
            var page = McpArguments.RequiredId(pageId, "pageId");
            var offset = McpCursor.Offset(cursor, "attachments");
            var list = await m_bus.InvokeAsync<PageAttachmentsDto>(new ListMcpAttachments(page), ct);
            var body = new StringBuilder();
            var items = new JsonArray();
            var shown = 0;
            foreach (var attachment in list.Items.Skip(offset).Take(ListPage))
            {
                var entry = Line(attachment);
                if (body.Length + entry.Length > McpText.Limit - 2_000)
                {
                    break;
                }
                body.Append(entry);
                items.Add(Json(attachment));
                shown++;
            }
            var more = offset + shown < list.Items.Count;
            var nextCursor = more ? McpCursor.Create("attachments", new JsonObject { ["o"] = offset + shown }) : null;
            var structured = new JsonObject { ["pageId"] = list.PageId, ["total"] = list.Items.Count, ["items"] = items };
            if (nextCursor is not null)
            {
                structured["nextCursor"] = nextCursor;
            }
            return McpText.Result(McpText.Wrap($"Přílohy stránky {list.PageId}: {shown} z {list.Items.Count}.",
                shown == 0 ? "Stránka nemá žádnou přílohu, kterou smíte otevřít." : body.ToString(),
                nextCursor is null ? null : $"Další přílohy: cursor \"{nextCursor}\"."), structured);
        }, ct);

    [McpServerTool(Name = "atlas_get_attachment", Title = "Údaje o příloze", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Údaje o jedné příloze (stránka, název, typ, velikost, použití), ne obsah souboru. attachmentId z atlas_list_attachments.")]
    [Description("Vrátí údaje o jedné příloze: stránku, název, MIME typ, velikost, SHA-256, čas nahrání, odkazy a použití v publikované " +
        "a pracovní verzi. Obsah souboru vrací atlas_download_attachment. " + McpText.TrustNotice)]
    public Task<CallToolResult> GetAttachmentAsync(
        [Description("attachmentId z atlas_list_attachments.")] string attachmentId,
        CancellationToken ct = default)
        => RunAsync("atlas_get_attachment", async () =>
        {
            var id = McpArguments.RequiredId(attachmentId, "attachmentId");
            var attachment = await m_bus.InvokeAsync<AttachmentInfoDto>(new GetMcpAttachment(id), ct);
            return McpText.Result(McpText.Wrap("Příloha:", Line(attachment)), Json(attachment));
        }, ct);

    [McpServerTool(Name = "atlas_download_attachment", Title = "Stáhnout přílohu", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [Description("Stáhne původní obsah přílohy (nikdy náhled) jako vložený binární zdroj (resource blob, base64) s názvem, MIME typem, " +
        "velikostí a SHA-256 celého souboru. Po částech: offset a length (nejvýše 4 MiB na volání, výchozí 4 MiB); dokud není complete, " +
        "volejte znovu s offset = nextOffset a části spojte za sebou. Shodu ověřte SHA-256 celého souboru (sha256), každá část má i chunkSha256. " +
        McpText.TrustNotice)]
    public Task<CallToolResult> DownloadAttachmentAsync(
        [Description("attachmentId z atlas_list_attachments.")] string attachmentId,
        [Description("Pozice prvního bajtu, výchozí 0.")] long offset = 0,
        [Description("Počet bajtů 1–4 194 304, výchozí 4 194 304.")] int length = MaximumChunkBytes,
        CancellationToken ct = default)
        => RunAsync("atlas_download_attachment", async () =>
        {
            var id = McpArguments.RequiredId(attachmentId, "attachmentId");
            if (length is < 1 or > MaximumChunkBytes)
            {
                return McpErrors.InvalidArgument("length musí být 1 až 4 194 304.");
            }
            var attachment = await m_bus.InvokeAsync<AttachmentInfoDto>(new GetMcpAttachment(id), ct);
            if (offset < 0 || offset > attachment.Size || offset == attachment.Size && attachment.Size > 0)
            {
                return McpErrors.InvalidArgument($"offset musí být 0 až {attachment.Size - 1} (velikost souboru je {attachment.Size} B).");
            }
            var bytes = new byte[(int)Math.Min(length, attachment.Size - offset)];
            try
            {
                await using var file = m_files.Open(id);
                file.Seek(offset, SeekOrigin.Begin);
                await file.ReadExactlyAsync(bytes, ct);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new ApiException(404, "Příloha není dostupná.");
            }
            return Downloaded(attachment, offset, bytes);
        }, ct);

    /// <summary>Strict standard base64 with padding: no whitespace, no URL alphabet, length a multiple of four, so the size is exact.</summary>
    internal static byte[] Decode(string encoded, bool image)
    {
        if (encoded.Length == 0 || encoded.Length % 4 != 0)
        {
            throw InvalidBase64();
        }
        var padding = encoded.EndsWith("==", StringComparison.Ordinal) ? 2 : encoded.EndsWith('=') ? 1 : 0;
        if (encoded.AsSpan(0, encoded.Length - padding).ContainsAnyExcept(Base64Alphabet))
        {
            throw InvalidBase64();
        }
        var size = encoded.Length / 4 * 3 - padding;
        if (size > MaximumUploadBytes)
        {
            throw TooLarge(image);
        }
        var bytes = new byte[size];
        if (!Convert.TryFromBase64String(encoded, bytes, out var written) || written != size)
        {
            throw InvalidBase64();
        }
        return bytes;
    }

    private static ApiException InvalidBase64()
        => new(400, "contentBase64 musí být standardní base64 s doplněním (=), bez mezer a zalomení řádků.", "invalid_argument");

    private static ApiException TooLarge(bool image)
        => new(413, image
                ? $"Obrázek je větší než 2 MiB, nejvíc co lze nahrát přes MCP. Obrázky do 10 MiB nahrajte přes REST: {RestUpload}."
                : $"Soubor je větší než 2 MiB, nejvíc co lze nahrát přes MCP. Přílohy do 25 MiB nahrajte přes REST: {RestUpload}.",
            "attachment_too_large", new Dictionary<string, object?> { ["maximumBytes"] = MaximumUploadBytes });

    private CallToolResult Uploaded(McpStoredAttachment stored, bool image, string sha256)
    {
        var attachment = stored.Attachment;
        var url = m_address.PageUrl(stored.PageId);
        var text = new StringBuilder();
        text.Append("Příloha nahrána. Stránka se nezměnila (text, verze ani publikace); přílohu vložíte Markdownem do pracovní verze: obrázek jako ![popis](url), soubor jako [název](downloadUrl), přes atlas_update_section nebo atlas_insert_section.\n");
        text.Append("- attachmentId: ").Append(attachment.Id).Append('\n');
        text.Append("- pageId: ").Append(stored.PageId).Append('\n');
        text.Append("- název: ").Append(McpText.Line(attachment.Name, 260)).Append('\n');
        text.Append("- MIME typ: ").Append(attachment.MimeType).Append(", velikost: ").Append(Bytes(attachment.Size)).Append('\n');
        text.Append("- sha256: ").Append(sha256).Append('\n');
        text.Append("- url (kanonický odkaz do obsahu): ").Append(attachment.Url).Append('\n');
        text.Append("- odkaz pro člověka na stránku: ").Append(url).Append('\n');
        var structured = new JsonObject
        {
            ["pageId"] = stored.PageId,
            ["spaceId"] = stored.SpaceId,
            ["attachmentId"] = attachment.Id,
            ["name"] = attachment.Name,
            ["size"] = attachment.Size,
            ["mimeType"] = attachment.MimeType,
            ["isImage"] = image,
            ["sha256"] = sha256,
            ["url"] = attachment.Url,
            ["downloadUrl"] = attachment.DownloadUrl,
            ["pageUrl"] = url
        };
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text.ToString() }],
            StructuredContent = JsonSerializer.SerializeToElement(structured)
        };
    }

    private static CallToolResult Downloaded(AttachmentInfoDto attachment, long offset, byte[] bytes)
    {
        var end = offset + bytes.Length;
        var complete = end >= attachment.Size;
        var chunkSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var text = new StringBuilder();
        text.Append(McpText.TrustNotice).Append("\n\n");
        text.Append(complete && offset == 0 ? "Celý soubor" : $"Část souboru: bajty {offset}–{end - 1} z {attachment.Size}")
            .Append(" je ve vloženém zdroji (base64 blob, původní obsah).\n");
        text.Append("<wiki-content>\n").Append(Line(attachment)).Append("</wiki-content>\n");
        text.Append(complete ? "Soubor je kompletní; ověřte SHA-256 celého souboru." : $"Pokračujte: offset {end}.").Append('\n');
        var structured = Json(attachment);
        structured["offset"] = offset;
        structured["length"] = bytes.Length;
        structured["chunkSha256"] = chunkSha256;
        structured["complete"] = complete;
        if (!complete)
        {
            structured["nextOffset"] = end;
        }
        structured["contentTrust"] = McpText.ContentTrust;
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = text.ToString() },
                new EmbeddedResourceBlock
                {
                    Resource = BlobResourceContents.FromBytes(bytes, $"atlas://attachments/{attachment.Id:D}", attachment.MimeType)
                }
            ],
            StructuredContent = JsonSerializer.SerializeToElement(structured)
        };
    }

    /// <summary>One attachment as a Markdown list entry; the name is wiki content.</summary>
    private static string Line(AttachmentInfoDto attachment)
    {
        var line = new StringBuilder();
        line.Append("- ").Append(McpText.Line(attachment.Name, 260)).Append(" (attachmentId: ").Append(attachment.Id).Append(")\n");
        line.Append("  ").Append(attachment.IsImage ? "obrázek" : "soubor").Append(", ").Append(attachment.MimeType).Append(", ")
            .Append(Bytes(attachment.Size)).Append(", nahráno ").Append(attachment.UploadedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))
            .Append('\n');
        line.Append("  v publikované verzi: ").Append(attachment.UsedInPublished ? "ano" : "ne");
        if (attachment.UsedInWorking is { } working)
        {
            line.Append(", v pracovní verzi: ").Append(working ? "ano" : "ne");
        }
        line.Append('\n');
        line.Append("  sha256: ").Append(attachment.Sha256).Append(", url: ").Append(attachment.Url).Append('\n');
        return line.ToString();
    }

    private static JsonObject Json(AttachmentInfoDto attachment)
    {
        var item = new JsonObject
        {
            ["attachmentId"] = attachment.Id,
            ["pageId"] = attachment.PageId,
            ["name"] = attachment.Name,
            ["mimeType"] = attachment.MimeType,
            ["size"] = attachment.Size,
            ["isImage"] = attachment.IsImage,
            ["sha256"] = attachment.Sha256,
            ["uploadedAt"] = attachment.UploadedAt,
            ["url"] = attachment.Url,
            ["downloadUrl"] = attachment.DownloadUrl,
            ["usedInPublished"] = attachment.UsedInPublished
        };
        if (attachment.UsedInWorking is { } working)
        {
            item["usedInWorking"] = working;
        }
        return item;
    }

    private static string Bytes(long size) => size.ToString("N0", CultureInfo.GetCultureInfo("cs-CZ")) + " B";

    private async Task<CallToolResult> RunAsync(string tool, Func<Task<CallToolResult>> body, CancellationToken ct)
    {
        try
        {
            return await body();
        }
        catch (ApiException error)
        {
            return McpErrors.From(error);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Never the exception text: an ORM or driver message can quote statement parameters, which hold tool arguments.
            m_logger.LogError("MCP tool {Tool} failed ({ErrorType})", tool, error.GetType().Name);
            return McpErrors.Error(500, "internal_error", "Nástroj se nepodařilo dokončit.");
        }
    }
}
