using Atlas.Server.Platform.Jobs;
using Atlas.Server.Features.Vaults.References;
using System.Text.Json;
using System.Text.Json.Nodes;
using NHibernate;
using NHibernate.Linq;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Domain;
using ISession = NHibernate.ISession;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Linking;
using Atlas.Server.Features.Search;

namespace Atlas.Server.Features.Pages.Commands;

public sealed class SavePageHandler
{
    private readonly ISession m_session;
    private readonly PageGate m_gate;
    private readonly CollaborationCheckpoints m_checkpoints;
    private readonly PageReader m_reader;
    private readonly SecretReferenceAccess m_references;
    private readonly IRequestChannel m_channel;
    private readonly ContactLinker m_contacts;
    private readonly EntityLinker m_linker;
    private readonly Outbox m_outbox;
    private readonly RequirementPageConversion m_conversion;
    private readonly JobQuiet m_quiet;

    public SavePageHandler(ISession session, PageGate gate, CollaborationCheckpoints checkpoints, PageReader reader,
        SecretReferenceAccess references, IRequestChannel channel, ContactLinker contacts, EntityLinker linker, Outbox outbox,
        RequirementPageConversion conversion, JobQuiet quiet)
    {
        m_quiet = quiet;
        m_conversion = conversion;
        m_outbox = outbox;
        m_linker = linker;
        m_contacts = contacts;
        m_channel = channel;
        m_session = session;
        m_gate = gate;
        m_checkpoints = checkpoints;
        m_reader = reader;
        m_references = references;
    }

    public async Task<PageDto> Handle(SavePage command, CancellationToken ct)
    {
        m_quiet.Edited();
        var request = command.Request;
        var channel = m_channel.Current;
        // A live save may leave its content out (the web's checkpoint): it is then the live document as the server projects it, below.
        var bodyless = string.IsNullOrEmpty(request.ContentJson) && request.CollaborationGeneration is not null;
        var (referenced, markdown) = bodyless ? (new HashSet<Guid>(), "") : Rules.ValidateDocument(request.ContentJson);
        // The rewrites below (the live projection, conversion, chips, contacts) validate once at their end, and only if they changed it.
        var validated = request.ContentJson;
        // A blank or too long title is refused before any statement; its characters are checked against the stored one below.
        Rules.Required(request.Title, "Název stránky", unchanged: request.Title?.Trim());
        // An MCP draft reaches this handler with the page already loaded: the gate takes the row as it is under the lock either
        // way. A page that moved to another space meanwhile answers 409 space_changed (PageGateMovedPageTests).
        var edited = (await m_gate.EditAsync(command.Id, AccessOperation.EditContent, ct)).Require();
        var (page, actor) = (edited.Page, edited.Actor);
        // The current revision and, in the same statement, the start of its version, which a checkpoint that continues the version
        // measures its changes against (RevisionVersions.PlaceAsync finds it in the session), and the published revision, which a save
        // that returns to it is compared with: a save stays within its statement budget.
        var current = m_session.Query<PageRevision>().Where(x => x.PageId == command.Id && x.ActiveTo == null).Select(x => x.BaseRevisionId);
        var publishedId = page.PublishedRevisionId ?? Guid.Empty;
        var loaded = await m_session.Query<PageRevision>()
            .Where(x => x.PageId == command.Id && (x.ActiveTo == null || current.Contains((Guid?)x.Id) || x.Id == publishedId))
            .ToListAsync(ct);
        var previous = loaded.Single(x => x.ActiveTo == null);
        // A live save carries the whole live document: a version another writer of the same session saved meanwhile (its checkpoint
        // is the current revision) is in it already, so it is no conflict unless the title, which is not live, differs (COL2-11).
        if (page.Version != request.ExpectedVersion && !(request.CollaborationGeneration is { } live
                && string.Equals(request.Title?.Trim(), previous.Title, StringComparison.Ordinal)
                && await m_checkpoints.IsCheckpointAsync(page.Id, live, previous.Id, ct)))
        {
            Rules.ExpectVersion(page.Version, request.ExpectedVersion);
        }
        if (request.CollaborationGeneration is { } generation)
        {
            var checkpoint = await m_checkpoints.ValidateCheckpointAsync(page, generation, previous.Id, null, ct);
            if (bodyless)
            {
                // The server's own projection of the shared log, as its checkpoint stores it (LivePageWrites), so the browser does not
                // serialize the whole page for each save. The writer's edits are in it: the web saves once they are acknowledged.
                request = request with { ContentJson = $"{{\"format\":\"{MarkdownDocumentValidator.Format}\",\"version\":1,\"plugins\":{FlushDocumentHandler.Plugins},\"document\":{checkpoint.EditorJson}}}" };
            }
            // The server writes the Markdown from this JSON (as FlushDocument does for the checkpoint), so the JSON alone decides; the
            // editor sends it without attributes at their default, the projection writes them all.
            else if (!JsonNode.DeepEquals(RichDocument.DocumentWithoutDefaults(request.ContentJson), RichDocument.DocumentWithoutDefaults(checkpoint.EditorJson)))
            {
                throw new ApiException(409, "Obsah se mezitím změnil. Počkejte na synchronizaci a uložte stránku znovu.", "collaboration_conflict");
            }
        }
        else
        {
            // A save without a generation (personal API, a web save without a lease) resets the generation below and deletes
            // its log; Page.Version does not move while others type, so only this guard keeps their unsaved edits.
            // Over mcp it keeps the token owner's own as well (D3).
            await m_checkpoints.RequireNoForeignUnsavedAsync(page.Id, actor.Id, ct, includeOwn: channel.RefusesOwnUnsaved);
            // Requirement blocks (or a version-3 party) are written as requirement headings. Not in a live save: its JSON must stay the
            // collaboration's document, which repair on open converted before the session began.
            if (await m_conversion.ConvertWriteAsync(page.Id, page.SpaceId, request.ContentJson, ct) is var shaped && shaped != request.ContentJson)
            {
                request = request with { ContentJson = shaped };
            }
            // Contacts of a Markdown write (MCP, the personal API) linked to the directory (contacts spec §3). Never in a live save: its JSON
            // must stay the collaboration's document, whose editor links them itself (the plugin's link option), as the web's other saves do.
            // Before them, the names of the blocks the write changed become chips (link: "auto"), which the contacts then link.
            if (command.FromMarkdown && command.Link && await m_linker.LinkWriteAsync(page.SpaceId, actor, previous.ContentJson, request.ContentJson, ct) is { } chips)
            {
                request = request with { ContentJson = chips };
            }
            if (command.FromMarkdown && await m_contacts.ResolveAsync(actor, request.ContentJson, ct) is { Changed: true } linked)
            {
                request = request with { ContentJson = linked.ContentJson };
            }
            // The web's save names a merged contact's target (contacts spec §5 point 4), so the merged one can go once nothing names it.
            else if (!command.FromMarkdown && await RedirectMergedContactsAsync(request.ContentJson, ct) is { } redirected)
            {
                request = request with { ContentJson = redirected };
            }
        }
        if (request.ContentJson != validated)
        {
            (referenced, markdown) = Rules.ValidateDocument(request.ContentJson);
        }
        // After the current revision: a legacy title it already holds still saves (Rules.Required).
        var title = Rules.Required(request.Title, "Název stránky", unchanged: previous.Title);
        // WP4 V6: an editor served a copy without hidden references cannot save over them, and adds only references it may use.
        await m_references.RequireEditableAsync(actor, previous.ContentJson, ct);
        await m_references.RequireUsableAsync(actor, SecretReferenceAccess.Added(referenced, previous.ContentJson), ct);
        if (command.FromMarkdown)
        {
            await AttachmentReferences.RequireAddedAsync(m_session, page.Id, previous.ContentJson, request.ContentJson, ct);
        }
        // Stored without attributes at their default, as the editor sends it; an older client, the API and the conversions above may
        // write them.
        request = request with { ContentJson = RichDocument.WithoutDefaultAttributes(request.ContentJson) };
        // The document decides, not its Markdown: a revision saved before the Markdown writer changed is the same page ("Zahodit
        // změny" sends it back, review W3-4), and only its stored projection is brought up to date in place.
        if (previous.Title == title && SameDocument(previous.ContentJson, request.ContentJson, live: request.CollaborationGeneration is not null))
        {
            if (previous.Markdown != markdown)
            {
                previous.Markdown = markdown;
                // The revision projected again in place may be the published one too.
                await (previous.Id == page.PublishedRevisionId
                    ? SearchDocuments.RefreshAsync(m_session, [page.Id], ct)
                    : SearchDocuments.RefreshWorkingAsync(m_session, [page.Id], ct));
            }
            if (request.CollaborationGeneration is null)
            {
                await m_checkpoints.ResetAsync(page, previous, ct);
            }
            else
            {
                await m_checkpoints.AdvanceCheckpointAsync(page, previous, ct, markSaved: true);
            }
            return await m_reader.ReadEditedAsync(edited, ct) with { Contacts = command.FromMarkdown ? m_contacts.Last : null, Links = command.FromMarkdown ? m_linker.Last : null };
        }
        if (page.Version == int.MaxValue)
        {
            throw new ApiException(409, "Byl dosažen limit verzí stránky.");
        }
        var boundary = Temporal.NextBoundary(previous.ActiveFrom, DateTime.UtcNow);
        previous.ActiveTo = boundary;
        await m_session.FlushAsync(ct);
        page.Version++;
        var revision = new PageRevision
        {
            Id = Guid.NewGuid(),
            PageId = command.Id,
            Version = page.Version,
            Title = title,
            ContentJson = request.ContentJson,
            Markdown = markdown,
            AuthorId = actor.Id,
            ActiveFrom = boundary,
            Channel = channel.Code,
            ApiTokenId = channel.ApiTokenId,
            AiAssisted = request.AiAssisted
        };
        var coauthors = request.CollaborationGeneration is { } saving ? await m_checkpoints.UnsavedAuthorsAsync(page.Id, saving, ct) : null;
        await RevisionVersions.PlaceAsync(m_session, m_outbox, page, previous, revision, false, ct, coauthors);
        // Exactly where its version started (a character typed and deleted again): no version, the history lists the revision it equals.
        // Exactly what readers see: the publication moves to it, so nothing is left to publish. The whole document decides, as above.
        var liveSave = request.CollaborationGeneration is not null;
        var start = revision.GroupId != revision.Id ? loaded.Find(x => x.Id == revision.BaseRevisionId) : null;
        if (start is not null && start.Title == title && SameDocument(start.ContentJson, request.ContentJson, liveSave))
        {
            revision.SameAsRevisionId = start.SameAsRevisionId ?? start.Id;
        }
        var published = loaded.Find(x => x.Id == page.PublishedRevisionId && x != previous);
        var republish = published is not null && (published == start ? revision.SameAsRevisionId is not null
            : published.Title == title && SameDocument(published.ContentJson, request.ContentJson, liveSave));
        await m_session.SaveAsync(revision, ct);
        // Its Markdown is the server's projection of its content: the next checkpoint compares against it without projecting it again.
        RevisionMarkdown.Remember(revision);
        m_outbox.PageRevised(page.Id, revision.Id);
        await SearchDocuments.RefreshWorkingAsync(m_session, [page.Id], ct);
        await AttachmentReferences.BindRevisionAsync(m_session, revision, ct);
        if (republish)
        {
            await PagePublications.CarryOverAsync(m_session, m_outbox, page, revision.Id, actor.Id, ct);
        }
        if (request.CollaborationGeneration is not null)
        {
            await m_checkpoints.AdvanceCheckpointAsync(page, revision, ct, markSaved: true);
        }
        else
        {
            await m_checkpoints.ResetAsync(page, revision, ct);
        }
        return await m_reader.ReadEditedAsync(edited, ct) with { Contacts = command.FromMarkdown ? m_contacts.Last : null, Links = command.FromMarkdown ? m_linker.Last : null };
    }

    /// <summary>The document with every contact merged into another (<c>mergedInto</c>) naming that one; null when none is.</summary>
    private async Task<string?> RedirectMergedContactsAsync(string contentJson, CancellationToken ct)
    {
        if (!contentJson.Contains("\"contact", StringComparison.Ordinal) && !contentJson.Contains("\"partyContactId\"", StringComparison.Ordinal))
        {
            return null;
        }
        var root = JsonNode.Parse(contentJson, documentOptions: new JsonDocumentOptions { MaxDepth = MarkdownDocumentValidator.JsonMaxDepth })!;
        // A requirement heading's party contact (shape §6) is redirected as a contact node's snapshot: its attributes hold the objectId.
        var parties = (root["document"]?["content"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(node => RichDocument.TypeOf(node) == "heading" && ContactSnapshots.Party(node["attrs"]) is not null).Select(node => (JsonObject)node["attrs"]!)
            .Select(attrs => (Contact: attrs, Key: "partyContactId"));
        var linked = ContactSnapshots.In(root["document"]).Select(contact => (Contact: contact, Key: "objectId")).Concat(parties)
            .Select(x => (x.Contact, x.Key, Id: x.Contact[x.Key] is JsonValue id && id.GetValueKind() == JsonValueKind.String
                && Guid.TryParse(id.GetValue<string>(), out var guid) ? guid : Guid.Empty))
            .Where(x => x.Id != Guid.Empty).ToArray();
        if (linked.Length == 0)
        {
            return null;
        }
        var targets = (await m_session.CreateSQLQuery("""
                SELECT "Id", CAST("Data" ->> 'mergedInto' AS uuid) AS "Into" FROM "ContentResource"
                WHERE "Id" = ANY(CAST(string_to_array(:ids, ',') AS uuid[])) AND "TypeId" = 'atlas.contact' AND "Data" ->> 'mergedInto' IS NOT NULL
                """)
            .AddScalar("Id", NHibernateUtil.Guid).AddScalar("Into", NHibernateUtil.Guid)
            .SetParameter("ids", string.Join(',', linked.Select(x => x.Id).Distinct()))
            .ListAsync<object[]>(ct)).ToDictionary(row => (Guid)row[0], row => (Guid)row[1]);
        var redirected = linked.Where(x => targets.ContainsKey(x.Id)).ToArray();
        foreach (var (contact, key, id) in redirected)
        {
            contact[key] = targets[id].ToString();
        }
        return redirected.Length == 0 ? null : root.ToJsonString(RichDocument.JsonWrite);
    }

    /// <summary>
    /// The same JSON value is the same document (D-M2): a stored revision written with escaped letters (a server or client serializer
    /// with the default encoder), another key order or its attributes at their default (saved before 3.6 stored them without) does not
    /// make an unchanged save a new revision. A live save (<paramref name="live"/>) declares the editor's plugins whatever the stored
    /// revision declared (an API write declares none): the session seeded from that revision, so its declarations are no change either.
    /// </summary>
    private static bool SameDocument(string stored, string requested, bool live)
    {
        if (stored == requested)
        {
            return true;
        }
        try
        {
            var before = JsonNode.Parse(RichDocument.WithoutDefaultAttributes(stored), documentOptions: MarkdownDocumentValidator.JsonOptions);
            var after = JsonNode.Parse(requested, documentOptions: MarkdownDocumentValidator.JsonOptions);
            if (live && before is JsonObject storedEnvelope && after is JsonObject liveEnvelope)
            {
                storedEnvelope.Remove("plugins");
                liveEnvelope.Remove("plugins");
            }
            return JsonNode.DeepEquals(before, after);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
