using System.Text.Json.Nodes;

namespace Atlas.Server.Features.Skills;

// Mapa znalostí (docs/skills-design.md): the reads and writes of people's topics. A topic is a page tag.

public sealed record ContactSkills(Guid ContactId, string Name, bool Self, IReadOnlyList<SkillView> Skills, IReadOnlyList<TopicRef> Suggested);

/// <summary>One topic of a person. <see cref="Score"/> 1–5 or null (nothing the reader may see as a number); <see cref="Peers"/> the
/// colleagues counted in it (0 below the threshold, <see cref="FewPeers"/> then says some rated it); <see cref="Raters"/> the signed ratings,
/// for a wiki admin every shared one; <see cref="Evidence"/> the readable pages of the topic the person wrote or is mentioned on.</summary>
public sealed record SkillView(Guid TagId, string Name, double? Score, int? Self, string? SelfNote, int Peers, bool FewPeers, int? Mine, string? MyVisibility, string? MyNote,
    IReadOnlyList<SkillRater> Raters, int Evidence, string? Group = null);

public sealed record SkillRater(string Name, int Level, string Visibility, string? Note);

/// <summary>The reader's rating of a person in a topic named by text (an existing topic, an alias or a new one); Level null removes it.
/// Visibility private (only the rater), shared (anonymously in the summary) or signed (with the rater's name; one's own is shared).</summary>
public sealed record RateSkillRequest(string Topic, int? Level, string? Visibility = null, string? Note = null);

/// <summary><see cref="SelfContactId"/>: the reader's own contact, for „Přidat sebe“ (null without one).</summary>
public sealed record ExpertList(TopicView? Topic, IReadOnlyList<TopicRef> Similar, IReadOnlyList<Expert> People, Guid? SelfContactId = null);

public sealed record TopicView(Guid Id, string Name, IReadOnlyList<string> Aliases, IReadOnlyList<TopicRef> Parents, IReadOnlyList<TopicRef> Children, IReadOnlyList<TopicRef>? Related = null);

/// <summary>Who knows what as a graph (skills spec §7): people and topics, person–topic links with the level the reader may see
/// (<see cref="SkillLink.Level"/> null below the anonymity threshold), topic–topic links part_of and related. <see cref="Truncated"/>: the
/// wiki has more than the graph shows (the topics most people know are kept).</summary>
public sealed record SkillGraph(IReadOnlyList<SkillNode> Nodes, IReadOnlyList<SkillLink> Links, bool Truncated);

/// <summary><see cref="Kind"/> person (Id the contact) or topic (Id the tag); <see cref="Organization"/> of a person, for the filter.</summary>
public sealed record SkillNode(Guid Id, string Kind, string Name, string? Title = null, string? Organization = null, int People = 0);

public sealed record SkillLink(Guid Source, Guid Target, string Kind, double? Level = null);

public sealed record TopicRef(Guid Id, string Name, int People, string? Matched = null);

/// <summary>A person for a topic: the directory contact, its skill in the topic (or <see cref="Via"/> a narrower one) and the pages of it.</summary>
public sealed record Expert(Guid ContactId, JsonObject Contact, SkillView? Skill, string? Via, bool InContext, int Evidence, IReadOnlyList<EvidencePage> Pages);

public sealed record EvidencePage(Guid PageId, string Title, string SpaceName);

public sealed record TopicSuggestions(IReadOnlyList<TopicRef> Items);

public sealed record MergeTopicsRequest(Guid IntoId);

public sealed record LinkTopicsRequest(Guid ParentId, bool Remove = false, string? Kind = null);
