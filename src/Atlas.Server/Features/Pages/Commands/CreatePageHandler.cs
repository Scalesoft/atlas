using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Vaults.References;
using Atlas.Server.Features.Projector.Domain;
using Atlas.Server.Platform.Content;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Features.PageDecorations.Domain;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Domain;
using ISession = NHibernate.ISession;

using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Linking;
using Atlas.Server.Features.Search;

namespace Atlas.Server.Features.Pages.Commands;

public sealed class CreatePageHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly PageReader m_reader;
    private readonly AttachmentFiles m_files;
    private readonly IAtlasLocks m_locks;
    private readonly SecretReferenceAccess m_references;
    private readonly IRequestChannel m_channel;
    private readonly ContactLinker m_contacts;
    private readonly EntityLinker m_linker;
    private readonly Outbox m_outbox;
    private readonly RequirementPageConversion m_conversion;

    public CreatePageHandler(ISession session, IContentAccess access, PageReader reader, AttachmentFiles files, IAtlasLocks locks,
        SecretReferenceAccess references, IRequestChannel channel, ContactLinker contacts, EntityLinker linker, Outbox outbox,
        RequirementPageConversion conversion)
    {
        m_conversion = conversion;
        m_outbox = outbox;
        m_linker = linker;
        m_contacts = contacts;
        m_channel = channel;
        m_references = references;
        m_session = session;
        m_access = access;
        m_reader = reader;
        m_files = files;
        m_locks = locks;
    }

    public async Task<PageDto> Handle(CreatePage command, CancellationToken ct)
    {
        var request = command.Request;
        var title = Rules.Required(request.Title, "Název stránky");
        var actor = await LockSpaceAsync(request.SpaceId, ct);
        if (request.IsTemplate)
            await m_access.RequireSpaceAsync(request.SpaceId, SpaceRole.Admin, ct);
        var template = request.TemplatePageId is { } templateId ? await ReadTemplateAsync(templateId, request.SpaceId, ct) : null;
        var contentJson = template?.ContentJson ?? request.ContentJson;
        if (template is not null && command.TemplateContentJson is { } read)
        {
            if (read != template.ContentJson)
                throw new ApiException(409, "Šablona se mezitím změnila; pošlete požadavek znovu.", "version_conflict");
            contentJson = request.ContentJson;
        }
        // A page from a deployment preset starts its requirements afresh (plan 3.1 §2.3), as a copy of a deployment does; the text test
        // keeps every other template unparsed.
        // shortcut: Pages calls the Projector domain directly; a page-create hook of the module replaces it when Projector leaves Atlas.Server.
        if (template is not null && (contentJson.Contains("\"requirement\"", StringComparison.Ordinal) || contentJson.Contains("\"state\"", StringComparison.Ordinal)))
        {
            var rich = JsonNode.Parse(contentJson, documentOptions: MarkdownDocumentValidator.JsonOptions)!.AsObject();
            RequirementTree.ResetForCopy(rich);
            RequirementTree.Normalize(rich);
            contentJson = rich.ToJsonString(RichDocument.JsonWrite);
        }
        // Requirement blocks (or a version-3 party) are written as requirement headings; the page's own id is set by Rehome below.
        contentJson = await m_conversion.ConvertWriteAsync(RequirementHeadings.NewPage, request.SpaceId, contentJson, ct);
        // A template is read through PageReader, so it keeps only references the actor may Discover (WP4 V6).
        var (referenced, markdown) = Rules.ValidateDocument(contentJson);
        await m_references.RequireUsableAsync(actor, referenced, ct);
        var parent = await AuthorizeParentAsync(request, ct);
        // The names of the written Markdown become chips (link: "auto"; after a template, only what was appended), then contacts of a
        // Markdown write are linked to the directory (contacts spec §3), once the actor may create here; the web links its own.
        if (command.FromMarkdown && command.Link && await m_linker.LinkWriteAsync(request.SpaceId, actor, template?.ContentJson, contentJson, ct) is { } chips)
        {
            (contentJson, markdown) = (chips, Rules.ValidateDocument(chips).Markdown);
        }
        if (command.FromMarkdown && await m_contacts.ResolveAsync(actor, contentJson, ct) is { Changed: true } linked)
        {
            (contentJson, markdown) = (linked.ContentJson, Rules.ValidateDocument(linked.ContentJson).Markdown);
        }
        // Copy-on-create (WP3 S4): a subpage starts with its parent's access mode and grants; a root page with the space defaults.
        var page = new Page
        {
            Id = Guid.NewGuid(), SpaceId = request.SpaceId, ParentId = request.ParentId, OwnerId = actor.Id,
            OwnAccess = parent?.OwnAccess ?? false, IsTemplate = request.IsTemplate, TemplatePageId = template?.Id
        };
        await m_session.SaveAsync(page, ct);
        await PageAccessModeHistory.RecordAsync(m_session, page, actor.Id, ct);
        // A new page starts with the icon and colour of its template, else of its parent (a copy; the user may change it after).
        var decoration = await InheritedDecorationAsync(template?.Id, parent?.Id, ct) is { } inherited
            ? new PageDecoration { PageId = page.Id, RevisionId = Guid.CreateVersion7(), Icon = inherited.Icon, Color = inherited.Color }
            : null;
        if (decoration is not null)
            await m_session.SaveAsync(decoration, ct);
        await PageVerifications.Commands.PageVerificationStart.StartAsync(m_session, page, ct);
        // The insert gets its SortOrder and writes the organization revision with the actor set by LockActorAsync (ContentRowEffects).
        await m_session.FlushAsync(ct);
        if (parent is not null)
        {
            // The lock on the space (LockSpaceAsync) keeps the parent's grants stable until commit.
            await PageItemAccess.CopyFromParentAsync(m_session, page.Id, parent.Id, actor.Id, ct);
        }
        if (template is not null && command.FromMarkdown && command.TemplateContentJson is not null)
        {
            // The appended Markdown adds no attachment: none belongs to this page yet but the copies, whose ids it cannot know.
            await AttachmentReferences.RequireAddedAsync(m_session, page.Id, template.ContentJson, contentJson, ct);
        }
        // A page written with its own id not yet known (a deployment from the catalog) links its requirement headings through the id of all f (RequirementHeadings.NewPage).
        if (RequirementHeadings.Rehome(contentJson, RequirementHeadings.NewPage, page.Id) is var own && own != contentJson)
        {
            (contentJson, markdown) = (own, Rules.ValidateDocument(own).Markdown);
        }
        IReadOnlyList<Guid> copies = [];
        if ((template?.Id ?? command.CopyAttachmentsOf) is { } source)
        {
            // A copy's links between its own requirement headings lead to the copy, not to the page it came from.
            if (RequirementHeadings.Rehome(contentJson, source, page.Id) is var rehomed && rehomed != contentJson)
            {
                (contentJson, markdown) = (rehomed, Rules.ValidateDocument(rehomed).Markdown);
            }
            (contentJson, copies) = await AttachmentCopies.CopyAsync(m_session, m_files, contentJson, [source], page.Id, actor.Id, ct);
        }
        else if (command.FromMarkdown)
        {
            await AttachmentReferences.RequireAddedAsync(m_session, page.Id, null, contentJson, ct);
        }
        var created = await AttachmentCopies.DiscardOnFailureAsync(m_files, copies, () => WriteRevisionAsync(page, actor, title, contentJson,
            // The copies' ids are in the JSON only, so the projection is written again from it.
            copies.Count > 0 ? null : markdown, ct));
        // The icon picker sends the decoration's revision; without it a change right after creating would answer 409.
        return created with { Contacts = command.FromMarkdown ? m_contacts.Last : null, Links = command.FromMarkdown ? m_linker.Last : null,
            Decoration = decoration is null ? null : new PageDecorationDto(decoration.RevisionId, decoration.Icon, decoration.Color) };
    }

    private async Task<PageDecoration?> InheritedDecorationAsync(Guid? templateId, Guid? parentId, CancellationToken ct)
    {
        foreach (var id in new[] { templateId, parentId })
            if (id is { } source && await m_session.GetAsync<PageDecoration>(source, ct) is { Icon: not null } decoration)
                return decoration;
        return null;
    }

    private async Task<PageDto> WriteRevisionAsync(Page page, UserAccount actor, string title, string contentJson, string? markdown, CancellationToken ct)
    {
        markdown ??= Rules.ValidateDocument(contentJson).Markdown;
        var revision = new PageRevision
        {
            Id = Guid.NewGuid(),
            PageId = page.Id,
            Version = page.Version,
            Title = title,
            ContentJson = contentJson,
            Markdown = markdown,
            AuthorId = actor.Id,
            ActiveFrom = DateTime.UtcNow,
            Channel = m_channel.Current.Code,
            ApiTokenId = m_channel.Current.ApiTokenId
        };
        await RevisionVersions.PlaceAsync(m_session, m_outbox, page, null, revision, false, ct);
        await m_session.SaveAsync(revision, ct);
        m_outbox.PageRevised(page.Id, revision.Id);
        await SearchDocuments.RefreshWorkingAsync(m_session, [page.Id], ct);
        await AttachmentReferences.BindRevisionAsync(m_session, revision, ct);
        return await m_reader.ReadAsync(page.Id, ct);
    }

    /// <summary>
    /// The locks of a page insert, in the order of OrganizationLocks and ImportGuards: advisory shared, space, actor. A caller that
    /// decides something of its own in this transaction before <see cref="Handle"/> takes them here instead of LockActorAsync.
    /// </summary>
    /// <remarks>
    /// A page's insert takes the space FOR UPDATE to order the siblings (ContentRowEffects). A create that held the space only
    /// FOR SHARE had to upgrade it there, and two of them in one space waited for each other's FOR SHARE (40P01).
    /// </remarks>
    public async Task<UserAccount> LockSpaceAsync(Guid spaceId, CancellationToken ct)
    {
        await m_locks.AcquireContentSharedAsync(ct);
        // A missing space is left to LockActorAsync and its 404.
        await m_locks.LockSpaceForUpdateAsync(spaceId, ct);
        return await m_access.LockActorAsync(spaceId, ct);
    }

    /// <summary>
    /// The decisions of a create before its write, for a caller that converts Markdown between a read transaction and the create
    /// (<see cref="PageMarkdownCreate"/>, MCP's atlas_create_draft): after <c>LockActorAsync</c> on the space, the same refusals as
    /// <see cref="Handle"/> (template flag Admin, D12 parent or Writer, the template) and the template as the actor reads it. The
    /// create decides all of it again under its own locks.
    /// </summary>
    public async Task<PageDto?> PrepareAsync(CreatePageRequest request, CancellationToken ct)
    {
        Rules.Required(request.Title, "Název stránky");
        if (request.IsTemplate)
            await m_access.RequireSpaceAsync(request.SpaceId, SpaceRole.Admin, ct);
        await AuthorizeParentAsync(request, ct);
        return request.TemplatePageId is { } templateId ? await ReadTemplateAsync(templateId, request.SpaceId, ct) : null;
    }

    private async Task<PageDto> ReadTemplateAsync(Guid templateId, Guid spaceId, CancellationToken ct)
    {
        var source = await m_session.GetAsync<Page>(templateId, ct);
        if (source is null || source.IsTrashed || source.SpaceId != spaceId || !source.IsTemplate)
            throw new ApiException(404, "Šablona nebyla nalezena.");
        var template = await m_reader.ReadAsync(templateId, ct);
        if (!template.IsTemplate || template.SpaceId != spaceId)
            throw new ApiException(404, "Šablona nebyla nalezena.");
        return template;
    }

    /// <summary>
    /// D12: a subpage needs EditContent on its parent (a reader of the parent is told 403), a root page Writer on the space (a space
    /// operation: a reader of the space is told 403, an account without a role there gets the space 404).
    /// </summary>
    private async Task<Page?> AuthorizeParentAsync(CreatePageRequest request, CancellationToken ct)
    {
        if (request.ParentId is not { } parentId)
        {
            await m_access.RequireSpaceAsync(request.SpaceId, SpaceRole.Writer, ct);
            return null;
        }
        var parent = await m_locks.ReadPageSharedAsync(parentId, ct);
        if (parent is null || parent.IsTrashed || parent.IsTemplate || parent.SpaceId != request.SpaceId)
        {
            throw await UnavailableParentAsync(request.SpaceId, ct);
        }
        var authorization = await m_access.AuthorizePageAsync(parent, AccessOperation.EditContent, ct);
        if (authorization.Outcome == PageAccessOutcome.Missing)
        {
            // A parent the account cannot read is indistinguishable from a missing one.
            throw await UnavailableParentAsync(request.SpaceId, ct);
        }
        if (authorization.Outcome == PageAccessOutcome.Forbidden)
        {
            throw new ApiException(403, "Podstránku můžete vytvořit jen pod stránkou, kterou smíte upravovat.");
        }
        return parent;
    }

    /// <summary>An account without Writer on the space gets the space's refusal (403 for its reader, else 404) and learns nothing about the parent.</summary>
    private async Task<ApiException> UnavailableParentAsync(Guid spaceId, CancellationToken ct)
    {
        await m_access.RequireSpaceAsync(spaceId, SpaceRole.Writer, ct);
        return new ApiException(400, "Rodičovská stránka není dostupná v tomto prostoru.");
    }
}
