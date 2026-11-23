using Atlas.Server.Features.Skills.Commands;
using Atlas.Server.Features.Skills.Queries;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Contacts.Persistence;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.PageTags.Domain;
using Atlas.Server.Features.PersonalLibrary.Domain;
using Atlas.Server.Platform.Persistence;

namespace Atlas.Server.Features.Skills;

/// <summary>
/// Kdo co umí (docs/skills-design.md): a person's topics, the rating, who knows a topic, merging and linking topics. Any signed-in reader may
/// rate any contact they may read and keep the topic catalog (owner decision 2026-11-23); the summary shows colleagues' ratings from
/// <see cref="Threshold"/> of them on, so nobody can tell who gave what, except a wiki admin, who sees every shared rating with its rater.
/// </summary>
public sealed class SkillsHandler(PersonalPageGuard guard, ContactDirectory directory, SkillStore store, PageTagStore tags, IAtlasLocks locks, IRequestChannel channel)
{
    /// <summary>The anonymity threshold K (owner decision 2026-11-23).</summary>
    public const int Threshold = 3;

    private const int MaxNote = 200;

    public async Task<ContactSkills> Handle(GetContactSkills query, CancellationToken ct)
    {
        var actor = await guard.LockIdentityAsync(null, false, ct);
        var (contactId, name, admin) = await ReadableAsync(actor, query.ContactId, ct);
        var summaries = await store.SummariesAsync(actor, admin, [contactId], null, ct);
        var evidence = await store.EvidenceAsync(actor, Ai(query.AiScopeOnly), [contactId], null, "", ct);
        var counts = evidence.GroupBy(x => x.TagId).ToDictionary(x => x.Key, x => x.Select(e => e.PageId).Distinct().Count());
        var skills = summaries.Select(x => View(x, admin, counts.GetValueOrDefault(x.TagId)))
            .OrderByDescending(x => x.Score ?? x.Mine ?? 0).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var self = await OwnContactAsync(actor, contactId, ct);
        // Suggestions only on one's own profile: topics of pages the person wrote, not rated yet, most pages first.
        var rated = skills.Select(x => x.TagId).ToHashSet();
        var suggested = self
            ? evidence.Where(x => !rated.Contains(x.TagId)).GroupBy(x => x.TagId)
                .Select(x => (Id: x.Key, Pages: x.Select(e => e.PageId).Distinct().Count())).OrderByDescending(x => x.Pages).Take(5).ToArray()
            : [];
        var suggestedTopics = new List<TopicRef>();
        foreach (var (id, pages) in suggested)
        {
            if (await store.TopicAsync(id, ct) is { } topic)
            {
                suggestedTopics.Add(new TopicRef(topic.Id, topic.Name, pages));
            }
        }
        return new ContactSkills(contactId, name, self, skills, suggestedTopics);
    }

    public async Task<ContactSkills> Handle(RateSkill command, CancellationToken ct)
    {
        var actor = await guard.LockIdentityAsync(null, false, ct);
        var request = command.Request;
        if (request.Level is < 1 or > 5)
        {
            throw new ApiException(400, "Úroveň je 1 až 5 (null hodnocení smaže).", "invalid_argument");
        }
        if (request.Visibility is not (null or "private" or "shared" or "signed"))
        {
            throw new ApiException(400, "Viditelnost je private, shared nebo signed.", "invalid_argument");
        }
        var (contactId, _, _) = await ReadableAsync(actor, command.ContactId, ct);
        var (name, normalized) = TagNames.Prepare([request.Topic ?? ""]).Single();
        var note = TagNames.Normalize(request.Note, MaxNote, true) is { Length: > 0 } text ? text : null;
        var self = await OwnContactAsync(actor, contactId, ct);
        // One's own rating is not signed: the name is the person's anyway.
        var visibility = request.Visibility ?? "shared";
        if (self && visibility == "signed")
        {
            visibility = "shared";
        }
        await locks.AcquireAdvisoryAsync("atlas.tag:catalog", ct);
        var tagId = await tags.ResolveAsync(name, normalized, actor, ct);
        await store.SaveAsync(contactId, tagId, actor.Id, request.Level, visibility, note, ct);
        return await Handle(new GetContactSkills(contactId), ct);
    }

    public async Task<ExpertList> Handle(FindExperts query, CancellationToken ct)
    {
        var actor = await guard.LockIdentityAsync(null, false, ct);
        if (query.Limit is < 1 or > 50)
        {
            throw new ApiException(400, "Počet je 1 až 50.", "invalid_argument");
        }
        var text = TagNames.Normalize(query.Topic, 200, false);
        var context = TagNames.Normalize(query.Context, 200, true);
        var role = TagNames.Normalize(query.Role, 200, true);
        var found = await store.SearchAsync(actor, Ai(query.AiScopeOnly), text, null, 6, ct);
        if (found.Count == 0 || await store.TopicAsync(found[0].Id, ct) is not { } topic)
        {
            return new ExpertList(null, found, []);
        }
        // The topic and its narrower ones: who knows C# knows a part of .NET.
        var topicIds = topic.Children.Select(x => x.Id).Prepend(topic.Id).ToArray();
        var admin = await store.IsWikiAdminAsync(actor, ct);
        var live = await directory.LiveAsync(actor, await store.CandidatesAsync(actor, Ai(query.AiScopeOnly), topicIds, ct), ct);
        var people = live.Values.Where(x => x is { State: "ok", ContactId: not null, Contact: not null })
            .DistinctBy(x => x.ContactId).ToDictionary(x => x.ContactId!.Value, x => x.Contact!);
        if (role.Length > 0)
        {
            people = people.Where(x => Folded(x.Value["title"]?.ToString()).Contains(Folded(role), StringComparison.Ordinal))
                .ToDictionary(x => x.Key, x => x.Value);
        }
        var ids = people.Keys.ToArray();
        var summaries = (await store.SummariesAsync(actor, admin, ids, topicIds, ct)).ToLookup(x => x.ContactId);
        var evidence = (await store.EvidenceAsync(actor, Ai(query.AiScopeOnly), ids, topicIds, context, ct)).ToLookup(x => x.ContactId);
        var experts = new List<Expert>();
        foreach (var (id, contact) in people)
        {
            var pages = evidence[id].DistinctBy(x => x.PageId).ToArray();
            var inContext = context.Length > 0 && (pages.Any(x => x.InContext)
                || Folded(contact["organization"]?.ToString()).Contains(Folded(context), StringComparison.Ordinal)
                || summaries[id].Any(x => Folded(x.SelfNote).Contains(Folded(context), StringComparison.Ordinal)));
            // The topic itself first, else the best of the narrower ones.
            var best = summaries[id].Select(x => (Summary: x, View: View(x, admin, pages.Count(p => p.TagId == x.TagId))))
                .OrderBy(x => x.Summary.TagId == topic.Id ? 0 : 1).ThenByDescending(x => x.View.Score ?? x.View.Mine ?? 0).FirstOrDefault();
            var shown = context.Length > 0 && pages.Any(x => x.InContext) ? pages.Where(x => x.InContext) : pages;
            experts.Add(new Expert(id, contact, best.View, best.View is { } view && view.TagId != topic.Id ? view.Name : null, inContext, pages.Length,
                shown.Take(3).Select(x => new EvidencePage(x.PageId, x.Title, x.SpaceName)).ToArray()));
        }
        var ranked = experts.OrderByDescending(x => x.InContext).ThenByDescending(x => x.Skill?.Score ?? 0).ThenByDescending(x => x.Evidence)
            .ThenByDescending(x => x.Skill?.Mine ?? 0).ThenBy(x => x.Contact["name"]?.ToString(), StringComparer.CurrentCultureIgnoreCase).Take(query.Limit).ToArray();
        // „Možná totéž“: other visible topics spelled like it.
        var similar = (await store.SearchAsync(actor, Ai(query.AiScopeOnly), topic.Name, topic.Id, 3, ct)).ToArray();
        return new ExpertList(topic, similar, ranked, await store.OwnContactAsync(actor.Id, ct));
    }

    public async Task<TopicSuggestions> Handle(SuggestTopics query, CancellationToken ct)
    {
        var actor = await guard.LockIdentityAsync(null, false, ct);
        return new TopicSuggestions(await store.SearchAsync(actor, Ai(query.AiScopeOnly), TagNames.Normalize(query.Query, 200, true), null, 10, ct));
    }

    public async Task<TopicView> Handle(MergeTopics command, CancellationToken ct)
    {
        var actor = await guard.LockIdentityAsync(null, false, ct);
        await locks.AcquireAdvisoryAsync("atlas.tag:catalog", ct);
        if (!await store.VisibleAsync(actor, Ai(false), command.TagId, ct) || !await store.VisibleAsync(actor, Ai(false), command.IntoId, ct))
        {
            throw new ApiException(404, "Téma nebylo nalezeno.", "topic_missing");
        }
        if (command.TagId == command.IntoId)
        {
            throw new ApiException(400, "Téma nelze sloučit samo se sebou.", "invalid_argument");
        }
        await store.MergeAsync(command.TagId, command.IntoId, actor.Id, ct);
        return (await store.TopicAsync(command.IntoId, ct))!;
    }

    public async Task<TopicView> Handle(LinkTopics command, CancellationToken ct)
    {
        var actor = await guard.LockIdentityAsync(null, false, ct);
        await locks.AcquireAdvisoryAsync("atlas.tag:catalog", ct);
        if (!await store.VisibleAsync(actor, Ai(false), command.TagId, ct) || !await store.VisibleAsync(actor, Ai(false), command.ParentId, ct))
        {
            throw new ApiException(404, "Téma nebylo nalezeno.", "topic_missing");
        }
        if (!command.Remove && await store.WouldCycleAsync(command.TagId, command.ParentId, ct))
        {
            throw new ApiException(400, "Téma nemůže být součástí sebe ani svého podtématu.", "invalid_argument");
        }
        await store.LinkAsync(command.TagId, command.ParentId, command.Remove, actor.Id, ct);
        return (await store.TopicAsync(command.TagId, ct))!;
    }

    /// <summary>The live contact the reader may read (a merge followed) and whether the reader is a wiki admin; else 404.</summary>
    private async Task<(Guid Id, string Name, bool Admin)> ReadableAsync(UserAccount actor, Guid contactId, CancellationToken ct)
    {
        var live = (await directory.LiveAsync(actor, [contactId], ct))[contactId];
        if (live is not { State: "ok", ContactId: { } id, Contact: { } contact })
        {
            throw new ApiException(404, "Kontakt nebyl nalezen.", "contact_missing");
        }
        return (id, contact["name"]?.ToString() ?? "", await store.IsWikiAdminAsync(actor, ct));
    }

    /// <summary>G5: MCP and the assistant see nothing of a space switched out of AI (RequestChannel.IsAiBound).</summary>
    private bool Ai(bool requested) => requested || channel.Current.IsAiBound;

    private Task<bool> OwnContactAsync(UserAccount actor, Guid contactId, CancellationToken ct) => store.IsOwnAsync(actor.Id, contactId, ct);

    private static SkillView View(SkillStore.Summary x, bool admin, int evidence)
    {
        var showPeers = x.Peers >= Threshold || admin && x.Peers > 0;
        double? score = showPeers
            ? ((x.Self ?? 0) + x.PeerAverage!.Value * x.Peers) / (x.Peers + (x.Self is null ? 0 : 1))
            : x.Self;
        return new SkillView(x.TagId, x.Name, score is { } value ? Math.Round(value, 1) : null, x.Self, x.SelfNote, showPeers ? x.Peers : 0,
            !showPeers && x.Peers > 0, x.Mine, x.MyVisibility, x.MyNote, x.Raters, evidence);
    }

    /// <summary>The fold of <see cref="SkillStore.Fold"/> for text in memory (role and context against a contact's fields).</summary>
    public static string Folded(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }
        var decomposed = value.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        return new string(decomposed.Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '#' or '+').ToArray());
    }
}
