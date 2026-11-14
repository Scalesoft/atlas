using Atlas.Server.Features.AiSuggestions;
using Atlas.Server.Features.Auth.Queries;
using Atlas.Server.Features.ModuleSettings.Queries;
using Atlas.Server.Platform.Content;
using Atlas.Server.Features.PageTags;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.AiWriting.Commands;

public sealed record WritingTemplate(Guid PageId, Guid RevisionId);
public sealed record WritingContext(Guid ActorId, string Stamp, Guid PageId, Guid SpaceId, int Version, string Text, IReadOnlyList<WritingTemplate> Templates, IReadOnlyList<TagDto> Tags)
{
    public override string ToString() => nameof(WritingContext);
}
public sealed record WritingRead(Guid PageId, string Task, string Text, int? ExpectedVersion = null, WritingContext? Expected = null)
{
    public override string ToString() => nameof(WritingRead);
}

/// <summary>Every request step gets a fresh actor and page under the readers' revocation locks. The model runs between steps.</summary>
public sealed class WritingReadHandler(ISession session, IContentAccess access, PageGate gate, SemanticSuggestionSql sql, PageTagStore tagStore, Atlas.Server.Features.Ai.Access.AiPolicyResolver policy)
{
    public async Task<WritingContext> Handle(WritingRead query, CancellationToken ct)
    {
        var opening = await gate.ReadAsync(query.PageId, AccessOperation.EditContent, ct);
        if (opening.Actor is null)
        {
            // No page has the id, and the gate locks no account then. This read answers a deactivated account 401 before the 404.
            await access.LockActorAsync(null, ct);
        }
        var opened = opening.RequireHidden();
        var (page, actor) = (opened.Page, opened.Actor);
        await RequirePolicyAsync(page, actor, query.Task, ct);
        RequireVersion(query, actor, page);
        if (query.Expected is { } revalidation) await RequireTemplatesAsync(opened.Space, revalidation, ct);
        var text = query.Text;
        IReadOnlyList<WritingTemplate> dependencies = [];
        if (query.Expected is null && query.Task == Atlas.Server.Features.Ai.Settings.AiTaskCatalog.EditorOutline)
        {
            var templates = await TemplateHeadingsAsync(opened.Space, ct);
            dependencies = templates.Dependencies;
            if (templates.Hints.Length > 0) text += "\n\nBezpečná struktura existujících šablon:\n" + templates.Hints;
        }
        if (query.ExpectedVersion is not null && query.Expected is null)
            text = await ChangedTextAsync(page, query.Task == Atlas.Server.Features.Ai.Settings.AiTaskCatalog.ChangeSummary, ct);
        var tags = query.Task == Atlas.Server.Features.Ai.Settings.AiTaskCatalog.TagSuggest
            ? (await tagStore.CandidatesInSpaceAsync(actor, page.SpaceId, ct)).Where(x => WritingValidation.Safe(x.Name)).ToArray()
            : [];
        return new WritingContext(actor.Id, actor.SecurityStamp, page.Id, page.SpaceId, page.Version, text, dependencies, tags);
    }


    private async Task RequirePolicyAsync(Page page, UserAccount actor, string task, CancellationToken ct)
    {
        if (!(await policy.ResolveAsync(actor.Id, ct)).Enabled || !await sql.SpaceInAiAsync(page.SpaceId, ct))
            throw Atlas.Server.Features.Ai.Capabilities.AiGate.Unavailable();
        if (task == Atlas.Server.Features.Ai.Settings.AiTaskCatalog.EditorIntent
            && (!await CurrentEditorAssist.IsEnabledAsync(session, ct)
                || (await CurrentAiPreference.LoadAsync(session, actor.Id, ct))?.EditorSuggestions is "off" or "rules"))
            throw new ApiException(404, "Stránka nebyla nalezena.");
    }

    private static void RequireVersion(WritingRead query, UserAccount actor, Page page)
    {
        if (query.ExpectedVersion is { } version && page.Version != version
            || query.Expected is { } expected && (expected.ActorId != actor.Id || expected.Stamp != actor.SecurityStamp
                || expected.SpaceId != page.SpaceId || expected.Version != page.Version))
            throw new ApiException(409, "Stránka se změnila.", "version_conflict");
    }

    private async Task<(string Hints, IReadOnlyList<WritingTemplate> Dependencies)> TemplateHeadingsAsync(LockedSpace space, CancellationToken ct)
    {
        var spaceId = space.SpaceId;
        var templates = await session.QueryOver<Page>().Where(x => x.SpaceId == spaceId && x.IsTemplate && !x.IsTrashed)
            .OrderBy(x => x.Id).Asc.ListAsync(ct);
        var hints = new List<string>();
        var dependencies = new List<WritingTemplate>();
        foreach (var candidate in templates)
        {
            // Under the locks of the page's space, in id order. A template that is gone, trashed, moved or unreadable is left out.
            var template = await ReadTemplateAsync(space, candidate.Id, ct);
            if (template is null) continue;
            var revision = await ReadableRevisionAsync(template, ct);
            if (revision is null) continue;
            var projection = RichMarkdownProjection.Project(RichDocument.Parse(revision.ContentJson), new ProjectionOptions(revision.Version));
            var headings = projection.Sections.Where(x => x.Level is 2 or 3 && x.Title.Length <= 100 && WritingValidation.Safe(x.Title))
                .Take(12).Select(x => new string('#', x.Level) + " " + x.Title).ToArray();
            if (headings.Length == 0) continue;
            hints.AddRange(headings);
            dependencies.Add(new WritingTemplate(template.Page.Id, revision.Id));
            if (dependencies.Count >= 3) break;
        }
        var text = string.Join("\n", hints);
        return (text.Length <= 1500 ? text : text[..1500], dependencies);
    }

    /// <summary>The space is the page's, and RequireVersion has already held it against the expected one.</summary>
    private async Task RequireTemplatesAsync(LockedSpace space, WritingContext expected, CancellationToken ct)
    {
        foreach (var dependency in expected.Templates)
        {
            var template = await ReadTemplateAsync(space, dependency.PageId, ct);
            if (template is null || (await ReadableRevisionAsync(template, ct))?.Id != dependency.RevisionId)
                throw new ApiException(409, "Podklady osnovy se změnily.", "version_conflict");
        }
    }

    /// <summary>A template of the space the account reads, FOR SHARE under the locks its page was opened with; null otherwise.</summary>
    private static async Task<LockedPage?> ReadTemplateAsync(LockedSpace space, Guid pageId, CancellationToken ct)
    {
        var opening = await space.ReadPageAsync(pageId, AccessOperation.ReadContent, ct);
        if (opening.State != PageOpeningState.Open) return null;
        var template = opening.Require();
        return template.Page.IsTemplate ? template : null;
    }

    /// <summary>The revision the gate's decision lets the account read: the working one for an editor, else the publication.</summary>
    private async Task<PageRevision?> ReadableRevisionAsync(LockedPage template, CancellationToken ct)
    {
        var templateId = template.Page.Id;
        return template.CanEdit
            ? await session.QueryOver<PageRevision>().Where(x => x.PageId == templateId && x.ActiveTo == null).SingleOrDefaultAsync(ct)
            : template.Page.PublishedRevisionId is { } published ? await session.GetAsync<PageRevision>(published, ct) : null;
    }

    private async Task<string> ChangedTextAsync(Page page, bool includePrevious, CancellationToken ct)
    {
        var current = await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync(ct);
        var published = page.PublishedRevisionId is { } id ? await session.GetAsync<PageRevision>(id, ct) : null;
        if (current is null) throw new ApiException(404, "Stránka nebyla nalezena.");
        var document = RichDocument.Parse(current.ContentJson);
        var old = published is null ? null : RichDocument.Parse(published.ContentJson);
        var parts = new List<string>();
        var count = includePrevious ? Math.Max(document.Blocks.Count, old?.Blocks.Count ?? 0) : document.Blocks.Count;
        for (var index = 0; index < count; index++)
        {
            var block = document.Blocks.ElementAtOrDefault(index);
            var previous = old?.Blocks.ElementAtOrDefault(index);
            if (RichDocument.StructurallyEqual(block, previous)) continue;
            if (includePrevious && SafeBlock(previous, page.Version) is { Length: > 0 } before)
                parts.Add("Dříve:\n" + before);
            if (SafeBlock(block, page.Version) is { Length: > 0 } after)
                parts.Add(includePrevious ? "Nově:\n" + after : after);
        }
        var text = string.Join("\n\n", parts);
        return text.Length <= 12_000 ? text : text[..12_000];
    }

    private static string SafeBlock(System.Text.Json.Nodes.JsonObject? block, int version)
    {
        if (block is null) return "";
        // The projection hides unsupported nodes and whole secret-bearing blocks; their metadata never goes out.
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["format"] = Atlas.Markdown.Sync.MarkdownDocumentValidator.Format, ["version"] = 1,
            ["document"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "doc", ["content"] = new System.Text.Json.Nodes.JsonArray(block.DeepClone()) }
        };
        var projection = RichMarkdownProjection.Project(RichDocument.Parse(json.ToJsonString()), new ProjectionOptions(version));
        return projection.Components.Count == 0 && WritingValidation.Safe(projection.Markdown) ? projection.Markdown.Trim() : "";
    }
}
