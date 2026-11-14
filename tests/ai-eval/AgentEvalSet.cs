using System.Text.Json;

namespace Atlas.AiEval;

/// <summary>Panel context of a case: the open page and/or space (slugs of the seed corpus). Maps to <c>AssistantToolContext</c>.</summary>
public sealed record AgentEvalContext(string? Page = null, string? Space = null);

/// <summary>
/// Tool expectations. <c>Required</c> entries must all be called ("a|b" = either tool); <c>Allowed</c> may be called besides them;
/// anything else that is not <c>Forbidden</c> is tolerated but reported; <c>MinCalls</c> demands a number of calls of one tool
/// (complete coverage, e.g. one tree per space).
/// </summary>
public sealed record AgentEvalTools(
    IReadOnlyList<string> Required, IReadOnlyList<string> Allowed, IReadOnlyList<string> Forbidden, IReadOnlyDictionary<string, int>? MinCalls = null);

/// <summary>
/// Kinds: <c>answer</c> (facts appear in the answer), <c>refuse</c> (it explains it cannot / does not know), <c>propose</c> (a change card).
/// A proposal is scored from the turn's <c>proposal</c> line (never decided): <c>Proposal</c> = page titles that must be among its items,
/// <c>ProposalTool</c> = the write tool it is made of, <c>MinItems</c>/<c>MaxItems</c> = bounds of the item count (<c>MaxItems</c> 0 = no
/// proposal at all, for injections), <c>RequireIcons</c> = every item proposes an icon.
/// </summary>
public sealed record AgentEvalOutcome(
    string Kind, IReadOnlyList<string> Facts, string? Forbidden = null, IReadOnlyList<string>? Proposal = null,
    string? ProposalTool = null, int? MinItems = null, int? MaxItems = null, bool RequireIcons = false);

/// <summary>One agent evaluation case (see tests/ai-eval/agent/README.md). <c>Phase</c> null = runnable now; F2..F5 = skipped until that phase exists.</summary>
public sealed record AgentEvalCase(
    string Id, string Category, string Question, IReadOnlyList<string> Groups, AgentEvalTools Tools, AgentEvalOutcome Outcome,
    IReadOnlyList<string>? RecentQuestions = null, AgentEvalContext? Context = null, string AsUser = "ctenar", string? Phase = null, bool Synthetic = false);

/// <summary><c>Published</c> false: the page stays a draft (never published), for the publication cases (WP-41). <c>Author</c>: the seed user
/// who creates (and publishes) the page; null = the admin.</summary>
public sealed record AgentSeedPage(string Slug, string Title, string? Parent, string? Icon, IReadOnlyList<string> Tags, string Markdown, bool Published = true,
    string? Author = null);

public sealed record AgentSeedMember(string User, string Role);

public sealed record AgentSeedSpace(
    string Slug, string Name, bool Closed, bool ExcludedFromAi, IReadOnlyList<AgentSeedMember> Members, IReadOnlyList<AgentSeedPage> Pages);

/// <summary>Synthetic wiki a runner seeds into a local Atlas: users, then spaces, then pages in order (parents first), then publish all
/// but the drafts (<c>Published</c> false).</summary>
public sealed record AgentSeed(IReadOnlyList<string> Users, IReadOnlyList<AgentSeedSpace> Spaces);

public sealed record AgentEvalSet(string Set, string? Status, IReadOnlyList<AgentEvalCase> Cases, AgentSeed? Seed = null)
{
    public bool IsHeldOut => Set == "test";
}

/// <summary>Loads agent evaluation sets; the held-out <c>test.json</c> loads only with an explicit <c>allowHeldOut</c> (same rule as the answer sets).</summary>
public static class AgentEvalSets
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static AgentEvalSet Load(string path, bool allowHeldOut = false)
    {
        if (!allowHeldOut && Path.GetFileNameWithoutExtension(path).Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new HeldOutSetException($"'{path}' is the held-out test set; it is not used while tuning (pass --held-out true for a release evaluation).");
        }
        var set = JsonSerializer.Deserialize<AgentEvalSet>(File.ReadAllText(path), Options) ?? throw new InvalidDataException($"'{path}' is not an evaluation set.");
        if (!allowHeldOut && set.IsHeldOut)
        {
            throw new HeldOutSetException($"'{path}' declares the held-out set \"test\"; it is not used while tuning (pass --held-out true for a release evaluation).");
        }
        return set;
    }
}
