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
/// Mapa znalostí (docs/skills-design.md): a person's topics, the rating, who knows a topic, merging and linking topics. Any signed-in reader may
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
        var summaries = await store.SummariesAsync(actor, admin, Ai(query.AiScopeOnly), [contactId], null, ct);
        var evidence = await store.EvidenceAsync(actor, Ai(query.AiScopeOnly), [contactId], null, "", ct);
        var counts = evidence.GroupBy(x => x.TagId).ToDictionary(x => x.Key, x => x.Select(e => e.PageId).Distinct().Count());
        var skills = summaries.Select(x => View(x, admin, counts.GetValueOrDefault(x.TagId))).Where(Shown)
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
            if (await store.TopicAsync(actor, Ai(query.AiScopeOnly), id, ct) is { } topic)
            {
                suggestedTopics.Add(new TopicRef(topic.Id, topic.Name, pages));
            }
        }
        return new ContactSkills(contactId, name, self, skills, suggestedTopics, self || await store.IsWikiAdminAsync(actor, ct));
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
        if (request.Everyone && request.Level is not null)
        {
            throw new ApiException(400, "Pro všechny jde téma jen odebrat (level null).", "invalid_argument");
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
        // Another spelling resolves to a tag the reader may know of only: a hidden one would confirm its name, so the typed one is its own topic.
        if (!await store.VisibleAsync(actor, Ai(false), tagId, ct))
        {
            tagId = await tags.CreateAsync(name, normalized, actor, ct);
        }
        if (request.Everyone)
        {
            // A topic on the person for everyone (a label anybody may put on anybody) goes when the person or a wiki admin says so.
            if (!self && !await store.IsWikiAdminAsync(actor, ct))
            {
                throw new ApiException(403, "Téma pro všechny odebere jen sám člověk nebo správce wiki.", "forbidden");
            }
            await store.RemoveTopicAsync(contactId, tagId, actor.Id, ct);
            return await Handle(new GetContactSkills(contactId), ct);
        }
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
        if (found.Count == 0 || await store.TopicAsync(actor, Ai(query.AiScopeOnly), found[0].Id, ct) is not { } topic)
        {
            return new ExpertList(null, found, []);
        }
        // The topic and its narrower ones: who knows C# knows a part of .NET.
        var topicIds = topic.Children.Select(x => x.Id).Prepend(topic.Id).ToArray();
        var admin = await AdminAsync(actor, ct);
        var live = await directory.LiveAsync(actor, await store.CandidatesAsync(actor, Ai(query.AiScopeOnly), topicIds, ct), ct);
        var people = live.Values.Where(x => x is { State: "ok", ContactId: not null, Contact: not null })
            .DistinctBy(x => x.ContactId).ToDictionary(x => x.ContactId!.Value, x => x.Contact!);
        if (role.Length > 0)
        {
            people = people.Where(x => Folded(x.Value["title"]?.ToString()).Contains(Folded(role), StringComparison.Ordinal))
                .ToDictionary(x => x.Key, x => x.Value);
        }
        var ids = people.Keys.ToArray();
        var summaries = (await store.SummariesAsync(actor, admin, Ai(query.AiScopeOnly), ids, topicIds, ct)).ToLookup(x => x.ContactId);
        var evidence = (await store.EvidenceAsync(actor, Ai(query.AiScopeOnly), ids, topicIds, context, ct)).ToLookup(x => x.ContactId);
        var experts = new List<Expert>();
        foreach (var (id, contact) in people)
        {
            var pages = evidence[id].DistinctBy(x => x.PageId).ToArray();
            var inContext = context.Length > 0 && (pages.Any(x => x.InContext)
                || Folded(contact["organization"]?.ToString()).Contains(Folded(context), StringComparison.Ordinal)
                || summaries[id].Any(x => Folded(x.SelfNote).Contains(Folded(context), StringComparison.Ordinal)));
            // The topic itself first, else the best of the narrower ones.
            var best = summaries[id].Select(x => (Summary: x, View: View(x, admin, pages.Count(p => p.TagId == x.TagId)))).Where(x => Shown(x.View))
                .OrderBy(x => x.Summary.TagId == topic.Id ? 0 : 1).ThenByDescending(x => x.View.Score ?? x.View.Mine ?? 0).FirstOrDefault();
            if (best.View is null && pages.Length == 0)
            {
                continue;
            }
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
        // A merge retags the pages of the merged topic: one on a page the reader may not edit (or, over MCP, of a space out of AI) is a wiki
        // admin's to merge, in the app, as page tags are a writer's.
        if (!await AdminAsync(actor, ct) && await store.TaggedBeyondAsync(actor, Ai(false), command.TagId, ct))
        {
            throw new ApiException(403, "Téma je i na stránkách, které nemůžete upravit; sloučí ho správce wiki.", "forbidden");
        }
        await store.MergeAsync(command.TagId, command.IntoId, actor.Id, ct);
        return (await store.TopicAsync(actor, Ai(false), command.IntoId, ct))!;
    }

    public async Task<TopicView> Handle(LinkTopics command, CancellationToken ct)
    {
        var actor = await guard.LockIdentityAsync(null, false, ct);
        await locks.AcquireAdvisoryAsync("atlas.tag:catalog", ct);
        if (!await store.VisibleAsync(actor, Ai(false), command.TagId, ct) || !await store.VisibleAsync(actor, Ai(false), command.ParentId, ct))
        {
            throw new ApiException(404, "Téma nebylo nalezeno.", "topic_missing");
        }
        if (command.Kind is not ("part_of" or "related"))
        {
            throw new ApiException(400, "Vazba je part_of (je součástí) nebo related (souvisí).", "invalid_argument");
        }
        if (command.TagId == command.ParentId)
        {
            throw new ApiException(400, "Téma nemůže souviset samo se sebou.", "invalid_argument");
        }
        if (!command.Remove && command.Kind == "part_of" && await store.WouldCycleAsync(command.TagId, command.ParentId, ct))
        {
            throw new ApiException(400, "Téma nemůže být součástí sebe ani svého podtématu.", "invalid_argument");
        }
        await store.LinkAsync(command.TagId, command.ParentId, command.Kind, command.Remove, actor.Id, ct);
        return (await store.TopicAsync(actor, Ai(false), command.TagId, ct))!;
    }

    /// <summary>At most this many topics in the graph: the ones most people know.</summary>
    private const int GraphTopics = 60;

    public async Task<SkillGraph> Handle(GetSkillGraph query, CancellationToken ct)
    {
        var actor = await guard.LockIdentityAsync(null, false, ct);
        var pairs = await store.GraphPairsAsync(actor, Ai(query.AiScopeOnly), query.SpaceId, query.ContactId, ct);
        var live = await directory.LiveAsync(actor, pairs.Select(x => x.ContactId).Distinct().ToArray(), ct);
        var people = live.Values.Where(x => x is { State: "ok", ContactId: not null, Contact: not null }).DistinctBy(x => x.ContactId)
            .ToDictionary(x => x.ContactId!.Value, x => x.Contact!);
        var admin = await AdminAsync(actor, ct);
        var summaries = (people.Count == 0 ? [] : await store.SummariesAsync(actor, admin, Ai(query.AiScopeOnly), people.Keys.ToArray(), pairs.Select(x => x.TagId).Distinct().ToArray(), ct))
            .Where(x => Shown(View(x, admin, 0))).ToList();
        var byTopic = summaries.GroupBy(x => x.TagId).OrderByDescending(x => x.Count()).ThenBy(x => x.First().Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var kept = byTopic.Take(GraphTopics).ToArray();
        var keptPeople = kept.SelectMany(x => x).Select(x => x.ContactId).ToHashSet();
        var nodes = new List<SkillNode>();
        nodes.AddRange(kept.Select(x => new SkillNode(x.Key, "topic", x.First().Name, People: x.Count())));
        nodes.AddRange(people.Where(x => keptPeople.Contains(x.Key)).Select(x => new SkillNode(x.Key, "person", x.Value["name"]?.ToString() ?? "",
            x.Value["title"]?.ToString(), x.Value["organization"]?.ToString())));
        var links = kept.SelectMany(x => x).Select(x => new SkillLink(x.ContactId, x.TagId, "skill", View(x, admin, 0) is var view ? view.Score ?? view.Mine : null)).ToList();
        links.AddRange((await store.RelationsAmongAsync(kept.Select(x => x.Key).ToArray(), ct)).Select(x => new SkillLink(x.TagId, x.ParentId, x.Kind)));
        return new SkillGraph(nodes, links, byTopic.Length > kept.Length);
    }

    /// <summary>The live contact the reader may read (a merge followed) and whether the reader is a wiki admin; else 404.</summary>
    private async Task<(Guid Id, string Name, bool Admin)> ReadableAsync(UserAccount actor, Guid contactId, CancellationToken ct)
    {
        var live = (await directory.LiveAsync(actor, [contactId], ct))[contactId];
        if (live is not { State: "ok", ContactId: { } id, Contact: { } contact })
        {
            throw new ApiException(404, "Kontakt nebyl nalezen.", "contact_missing");
        }
        return (id, contact["name"]?.ToString() ?? "", await AdminAsync(actor, ct));
    }

    /// <summary>Whether the reader sees every shared rating with its rater: a wiki admin, and only in the app. A token (/api/v1, MCP) and the
    /// assistant never get who gave what (the text may go to a model; atlas_find_experts promises it).</summary>
    private async Task<bool> AdminAsync(UserAccount actor, CancellationToken ct)
        => channel.Current is { ApiTokenId: null, IsAiBound: false } && await store.IsWikiAdminAsync(actor, ct);

    /// <summary>G5: MCP and the assistant see nothing of a space switched out of AI (RequestChannel.IsAiBound).</summary>
    private bool Ai(bool requested) => requested || channel.Current.IsAiBound;

    private Task<bool> OwnContactAsync(UserAccount actor, Guid contactId, CancellationToken ct) => store.IsOwnAsync(actor.Id, contactId, ct);

    private static SkillView View(SkillStore.Summary x, bool admin, int evidence)
    {
        // The anonymous ratings (shared, of others than the reader, who knows their own) count only when there are none or Threshold of
        // them: beside the signed ones (listed with their levels) one or two could be worked out of an average. Else the signed ones alone.
        // Watched over time, an exact count and average would give away each new anonymous rating: a reader's number takes the anonymous
        // ratings by band only (SkillStore.Bands), so it moves when the count crosses into the next band, never by one rating inside it.
        var anonymous = admin || x.Anonymous is 0 or >= Threshold;
        var (peers, average) = admin ? (x.Peers, x.PeerAverage)
            : anonymous && x.Band > 0 ? (x.Signed + x.Band, ((x.SignedAverage ?? 0) * x.Signed + x.BandAverage!.Value * x.Band) / (x.Signed + x.Band))
            : (x.Signed, x.SignedAverage);
        var showPeers = admin && peers > 0 || peers >= Threshold;
        double? score = showPeers
            ? ((x.Self ?? 0) + average!.Value * peers) / (peers + (x.Self is null ? 0 : 1))
            : x.Self;
        return new SkillView(x.TagId, x.Name, score is { } value ? Math.Round(value, 1) : null, x.Self, x.SelfNote, showPeers ? peers : 0,
            !showPeers && x.Peers > 0, x.Mine, x.MyVisibility, x.MyNote, x.Raters, evidence, x.Group, showPeers && !admin && anonymous && x.Band > 0);
    }

    /// <summary>A topic with something the reader may see: a number, the person's own estimate, the reader's rating or a named rater.
    /// One or two anonymous ratings alone list nothing: else anybody could put an unremovable label on anybody.</summary>
    private static bool Shown(SkillView view) => view.Score is not null || view.Self is not null || view.Mine is not null || view.Raters.Count > 0;

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
