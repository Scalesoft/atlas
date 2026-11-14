using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync.Recognition;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Linking;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Linking.Commands;

/// <summary>POST /api/pages/{id}/references: <paramref name="Choice"/> <c>{"targetId": …}</c> links the occurrence, <c>"text"</c> keeps
/// it plain text: an unresolved chip becomes its text, plain text stays as it is. Nothing is remembered outside the page (owner
/// decision: the page alone says what is linked), so plain text is offered again.</summary>
public sealed record ResolveReferenceRequest(int ExpectedVersion, int Ordinal, string? Text, JsonElement Choice) : Collaboration.IExpectedVersion;

public sealed record ResolveReference(Guid PageId, ResolveReferenceRequest Request);

/// <summary>POST /api/v1/pages/{id}/link: every whole name of one person a chip, of several an unresolved chip; the rest reported.</summary>
public sealed record LinkPageRequest(int ExpectedVersion) : Collaboration.IExpectedVersion;

public sealed record LinkPage(Guid PageId, int ExpectedVersion);

/// <summary>A linking write: the page's version after it, whether it wrote a revision (<paramref name="Changed"/>) and whether that went
/// straight into the published version (<paramref name="Published"/>: a link-only change of a page without a pending draft).</summary>
public sealed record ReferenceWriteResult(Guid PageId, Guid SpaceId, int Version, int? PublishedVersion, bool Changed, bool Published, LinkReport Links);

/// <summary>
/// The linking writes of a page (owner decision R2 and the link-only rule): the occurrences of its working revision as the extraction
/// reads them (the ordinals of <c>ContentReference</c>), what the actor may see, the chips made in place and the save through the shared
/// <see cref="SavePageHandler"/> at the expected version. A change that only turns text into chips goes straight into the published
/// version when the page had no pending draft (a new revision, published with a summary naming it, so history shows it and restoring
/// the previous revision undoes it); with a pending draft it stays in the draft. The graph is refreshed in the same transaction.
/// </summary>
public sealed class LinkPageHandler(ISession session, PageGate gate, IRequestChannel channel, EntityLinker linker, SavePageHandler save,
    PublishPageHandler publish, ContentReferenceStore store)
{
    public async Task<ReferenceWriteResult> Handle(LinkPage command, CancellationToken ct)
    {
        var (opened, working) = await OpenAsync(gate, session, channel, command.PageId, command.ExpectedVersion, ct);
        var (root, occurrences) = ContentReferences.Read(working.ContentJson, await linker.ExtractionLexiconAsync(opened.Page.SpaceId, ct), command.PageId);
        var actor = await linker.ActorLexiconAsync(opened.Page.SpaceId, opened.Actor, ct);
        var (changed, report) = EntityLinker.Auto(occurrences, actor, ordinals: true);
        return await WriteAsync(save, publish, store, session, channel, opened, working, changed ? root : null, report, ct);
    }

    internal static async Task<(EditablePage Opened, PageRevision Working)> OpenAsync(PageGate gate, ISession session, IRequestChannel channel, Guid pageId,
        int expectedVersion, CancellationToken ct)
    {
        EditablePage opened;
        try
        {
            opened = (await gate.EditAsync(pageId, AccessOperation.EditContent, ct)).Require();
        }
        catch (ApiException error) when (error.Status == 403 && channel.Current.ApiTokenId is not null)
        {
            // As the MCP content tools: a page the token's owner may read but not edit is the 404 of a working revision it may not see.
            throw new ApiException(404, LinkingScope.MissingPage);
        }
        await LinkingScope.RequireAsync(session, channel, opened.Page.SpaceId, LinkingScope.MissingPage, ct);
        Rules.ExpectVersion(opened.Page.Version, expectedVersion);
        var working = await session.QueryOver<PageRevision>().Where(x => x.PageId == pageId && x.ActiveTo == null).SingleOrDefaultAsync(ct)
            ?? throw new ApiException(404, LinkingScope.MissingPage);
        return (opened, working);
    }

    internal static async Task<ReferenceWriteResult> WriteAsync(SavePageHandler save, PublishPageHandler publish, ContentReferenceStore store, ISession session,
        IRequestChannel channel, EditablePage opened, PageRevision working, JsonNode? root, LinkReport report, CancellationToken ct)
    {
        var page = opened.Page;
        if (root is null)
        {
            return new ReferenceWriteResult(page.Id, page.SpaceId, page.Version, await PublishedVersionAsync(session, page, ct), false, false, report);
        }
        var publishedBefore = page.PublishedRevisionId == working.Id;
        // FromMarkdown: the contacts of the new chips are linked to the directory as a Markdown write's are (an account's contact, R3).
        var saved = await save.Handle(new SavePage(page.Id, new SavePageRequest(working.Title, ContentReferences.Write(root), null, page.Version),
            FromMarkdown: true, Link: false), ct);
        var published = false;
        if (publishedBefore && saved.Version != working.Version)
        {
            var summary = channel.Current.Channel switch
            {
                ContentChannel.Mcp => "Propojeno přes MCP: text beze změny, jen odkazy na lidi a stránky.",
                ContentChannel.Api => "Propojeno přes API: text beze změny, jen odkazy na lidi a stránky.",
                _ => "Propojeno: text beze změny, jen odkazy na lidi a stránky.",
            };
            // The publication refreshes the page row from the database: the save's new version must be there first.
            await session.FlushAsync(ct);
            saved = await publish.Handle(new PublishPage(page.Id, new PublishPageRequest(saved.Version, summary)), ct);
            published = true;
        }
        await session.FlushAsync(ct);
        await store.RefreshPageAsync(page.Id, null, ct);
        return new ReferenceWriteResult(page.Id, page.SpaceId, saved.Version, saved.PublishedVersion, true, published, report);
    }

    private static async Task<int?> PublishedVersionAsync(ISession session, Page page, CancellationToken ct) => page.PublishedRevisionId is { } id
        ? (await session.GetAsync<PageRevision>(id, ct))?.Version
        : null;
}

public sealed class ResolveReferenceHandler(ISession session, PageGate gate, IRequestChannel channel, EntityLinker linker, SavePageHandler save,
    PublishPageHandler publish, ContentReferenceStore store)
{
    public async Task<ReferenceWriteResult> Handle(ResolveReference command, CancellationToken ct)
    {
        var request = command.Request;
        var (opened, working) = await LinkPageHandler.OpenAsync(gate, session, channel, command.PageId, request.ExpectedVersion, ct);
        var (root, occurrences) = ContentReferences.Read(working.ContentJson, await linker.ExtractionLexiconAsync(opened.Page.SpaceId, ct), command.PageId);
        var occurrence = occurrences.FirstOrDefault(x => x.Ordinal == request.Ordinal);
        var choice = request.Choice;
        // Idempotent: the same pick again finds the chip it made, whose text is the person's name now.
        if (occurrence is { State: "linked" } && choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("targetId", out var again)
            && again.TryGetGuid(out var same) && occurrence.TargetId == same)
        {
            return await LinkPageHandler.WriteAsync(save, publish, store, session, channel, opened, working, null, LinkReport.Empty, ct);
        }
        if (occurrence is null || occurrence.Text != request.Text)
        {
            throw new ApiException(409, "Zmínka na tomto místě má jiný text; načtěte nepropojené zmínky znovu.", "reference_changed",
                new Dictionary<string, object?> { ["currentText"] = occurrence?.Text });
        }
        if (choice.ValueKind == JsonValueKind.String && choice.GetString() == "text")
        {
            if (occurrence.State == "linked")
            {
                throw new ApiException(409, "Zmínka je už propojená.", "reference_linked");
            }
            // Plain text stays as it is: nothing changes and nothing is remembered beside the page.
            var converted = ContentReferences.ToText(occurrence);
            return await LinkPageHandler.WriteAsync(save, publish, store, session, channel, opened, working, converted ? root : null, LinkReport.Empty, ct);
        }
        if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("targetId", out var target) || !target.TryGetGuid(out var targetId))
        {
            throw new ApiException(400, "choice je {\"targetId\": \"…\"}, nebo \"text\".", "invalid_argument");
        }
        if (occurrence.State == "linked")
        {
            throw new ApiException(409, "Zmínka je už propojená s někým jiným.", "reference_linked");
        }
        var actor = await linker.ActorLexiconAsync(opened.Page.SpaceId, opened.Actor, ct);
        var entity = new[] { "object:", "user:", "page:" }.Select(prefix => actor.Entities.GetValueOrDefault(prefix + targetId)).OfType<PlaceEntity>().FirstOrDefault()
            ?? throw new ApiException(404, "Cíl nebyl nalezen: kontakt, účet nebo stránka tohoto prostoru, které smíte číst.", "target_not_found");
        if (entity.Kind != occurrence.Kind)
        {
            throw new ApiException(400, "Zmínka je druhu {kind}, cíl druhu {kind1}.", "invalid_argument", args: new Dictionary<string, object?> { ["kind"] = occurrence.Kind, ["kind1"] = entity.Kind });
        }
        if (!ContentReferences.Link(occurrence, entity.Kind == "page" ? ContentReferences.PageChip(entity) : ContentReferences.PersonChip(entity)))
        {
            throw new ApiException(409, "Zmínka je rozdělená formátováním; propojte ji v editoru.", "reference_split");
        }
        var report = new LinkReport([new LinkedReference(entity.Kind, occurrence.Text, entity.Id, entity.Label)], [], [], []);
        return await LinkPageHandler.WriteAsync(save, publish, store, session, channel, opened, working, root, report, ct);
    }
}

/// <summary>One pass of the sweep (<see cref="ContentReferenceStore.SweepAsync"/>); the job sends it, tests send it themselves.</summary>
public sealed record SweepContentReferences;

public static class SweepContentReferencesHandler
{
    public static Task<int> Handle(SweepContentReferences command, ContentReferenceStore store, CancellationToken ct) => store.SweepAsync(ct);
}
