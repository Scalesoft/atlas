using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Atlas.Server.Features.Attachments.Commands;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Queries;
using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.Mcp.Tools;

/// <summary>
/// The presentation of an embed "on the side" of the Markdown: images and links to attachments are written as Markdown by the
/// content tools (<c>![popis](/api/attachments/{id}/content)</c>, <c>[název](/api/attachments/{id}/download)</c>), which carries no
/// width or alignment, and a file card stays a placeholder. atlas_update_embed changes them with fixed arguments, without the client
/// sending the rich document. The same steps as a content tool: <see cref="GetMcpEditBase"/> (G5, EditContent, hidden password
/// references, the expected version), then <see cref="SaveMcpEmbed"/>, which checks the replacement under the page lock and saves
/// through the shared embed handler.
/// </summary>
public sealed partial class McpDraftTools
{
    [McpServerTool(Name = "atlas_update_embed", Title = "Upravit vzhled obrázku nebo přílohy", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [Description("Upraví vložený obrázek nebo kartu přílohy v pracovní verzi bez přepisu textu: u obrázku alternativní text, šířku a zarovnání; u " +
        "obrázku i karty náhradu jinou přílohou stejné stránky (replaceWithAttachmentId; obrázek jen obrázkem). Náhrada je nový soubor, starší verze " +
        "stránky dál ukazují původní. Obrázek vkládejte a odebírejte Markdownem (![popis](/api/attachments/{id}/content)) a adresujte ho attachmentId " +
        "z této adresy v textu atlas_get_page; kartu přílohy attachmentId z jejích komponent. occurrence (pořadí v dokumentu od 1) jen když je stejná " +
        "příloha na stránce víckrát. Použijte expectedVersion z posledního čtení pracovní verze. " + DraftNotice)]
    public Task<CallToolResult> UpdateEmbedAsync(
        [Description("pageId stránky.")] string pageId,
        [Description("expectedVersion z posledního čtení pracovní verze.")] int expectedVersion,
        [Description("attachmentId vloženého obrázku (z adresy v Markdownu) nebo karty přílohy (z komponent atlas_get_page).")] string attachmentId,
        [Description("Pořadí vložení té přílohy na stránce od 1; jen když je vložená víckrát.")] int? occurrence = null,
        [Description("Nový alternativní text obrázku.")] string? alt = null,
        [Description("Nová šířka obrázku v procentech, 10–100.")] int? width = null,
        [Description("Nové zarovnání obrázku: \"left\", \"center\" nebo \"right\".")] string? align = null,
        [Description("attachmentId přílohy, která vložení nahradí (nahraná k této stránce).")] string? replaceWithAttachmentId = null,
        CancellationToken ct = default)
        => RunAsync("atlas_update_embed", async () =>
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            var attachment = McpArguments.RequiredId(attachmentId, "attachmentId");
            var replacement = McpArguments.OptionalId(replaceWithAttachmentId, "replaceWithAttachmentId");
            var edit = new UpdatePageEmbed(id, expectedVersion, attachment, occurrence, new ImageLook(alt, width, align), replacement);
            RequireVersion(edit.ExpectedVersion);
            EditPageEmbedHandler.RequireArguments(edit);
            // The read refuses what the save would: G5, no EditContent (404), hidden password references, a different version.
            await m_bus.InvokeAsync<McpEditBase>(new GetMcpEditBase(edit.PageId, edit.ExpectedVersion, MarkdownLinkRequest.None), ct);
            var written = await m_bus.InvokeAsync<McpWriteResult>(new SaveMcpEmbed(edit), ct);
            return Written(written.Version == edit.ExpectedVersion ? "Obsah je beze změny." : "Vložení upraveno.", written, []);
        }, ct);
}
