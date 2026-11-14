using Atlas.Server.Features.Ai.Settings.Domain;

namespace Atlas.Server.Features.Ai.Settings;

/// <summary>What text a task sends to the model. Only <see cref="WorkingText"/> (an author's unpublished text) needs the
/// separate permission to reach a cloud profile (Z2/D3); published content goes wherever the admin pointed the profile.</summary>
public enum AiInputClass
{
    None,
    Published,
    Query,
    WorkingText,
}

/// <param name="Code">Stable task code, also the <c>Task</c> (purpose) of an <c>AiUsage</c> row.</param>
/// <param name="ProfileKind">The <see cref="AiProfileKind"/> the task calls.</param>
/// <param name="Capability">The key of <c>GET /api/ai/capabilities</c> the task contributes to; null for internal tasks.</param>
/// <param name="Group">The group in Administrace → AI → Funkce (<c>search</c>, <c>read</c>, <c>write</c>, <c>create</c>);
/// null for tasks that are not switchable.</param>
/// <param name="Switchable">False for tasks governed elsewhere: indexing by its generation, the admin tests by the admin.</param>
/// <param name="RequiresIndex">A chat task over the stored chunks of the active index generation (plan phase 3 R3): it needs an
/// active generation, but not one built by the current embeddings revision, because the chunk text is read without a model.</param>
/// <param name="Timeout">The task's own limit on one model call; the pipeline uses min(profile, task) and reports an expiry to
/// the circuit breaker as <c>timeout</c> (plan phase 3 R6, design §8.3). Null = the profile's timeout.</param>
/// <param name="SlotWait">How long an interactive call waits for a concurrency slot before it fails with <c>busy</c> (R6);
/// zero = not at all, as for every task before phase 3.</param>
/// <param name="Limitable">The admin may set a daily limit per user in Administrace → AI → Funkce (plan phase 3 Q3).</param>
/// <param name="LatencyBudget">The <paramref name="Timeout"/> is how long the result is still useful, not a verdict on the server: its
/// expiry fails the call without counting towards the circuit breaker (AI4-7, inline suggestions while typing).</param>
public sealed record AiTaskDefinition(string Code, string ProfileKind, AiInputClass Input, string? Capability, string? Group,
    bool EnabledByDefault, bool Switchable, bool RequiresIndex = false, TimeSpan? Timeout = null, TimeSpan SlotWait = default,
    bool Limitable = false, bool LatencyBudget = false);

/// <summary>
/// The AI tasks of this version (design §8.1), a registry in code, not in the database. Their switches are one
/// <see cref="AiFeatureSettings"/> revision. Later phases only add rows (<c>search.answer</c>, <c>chat.answer</c>,
/// <c>page.summary</c>, <c>editor.intent</c> …); a stored revision that does not mention them uses their default.
/// </summary>
public static class AiTaskCatalog
{
    public const string SearchEmbedQuery = "search.embed-query";
    public const string PageRelated = "page.related";
    public const string EditorLink = "editor.link";
    public const string PageCreateSuggest = "page.create-suggest";
    public const string IndexEmbed = "index.embed";
    public const string AdminTestEmbedding = "admin.test-embedding";
    public const string AdminEval = "admin.eval";
    public const string AdminTestChat = "admin.test-chat";
    /// <summary>Odpověď z wiki in the omnisearch (S5, capability <c>answer</c>).</summary>
    public const string SearchAnswer = "search.answer";
    /// <summary>The assistant panel and every follow-up question (C1–C3, R4; capability <c>assistant</c>).</summary>
    public const string ChatAnswer = "chat.answer";
    public const string PageSummary = "page.summary";
    public const string EditorIntent = "editor.intent";
    public const string EditorTransform = "editor.transform";
    public const string EditorOutline = "editor.outline";
    public const string PublishCheck = "publish.check";
    public const string ChangeSummary = "publish.change-summary";
    public const string TagSuggest = "page.tag-suggest";
    public const string AssistantAgent = "assistant.agent";
    public const string AssistantWorkingText = "assistant.working-text";
    /// <summary>Calls no model: whether the agent may propose the left menu of a space as one batch of moves (atlas_arrange_pages).</summary>
    public const string AssistantArrange = "assistant.arrange-pages";
    /// <summary>Reorders the candidate sources of an answer (search.answer, chat.answer) by a cross-encoder.</summary>
    public const string SearchRerank = "search.rerank";
    public const string AdminTestRerank = "admin.test-rerank";

    /// <summary>The longest a rerank call may take; the callers wait far less (AnswerRerank.Budget) and keep today's order.</summary>
    public static readonly TimeSpan RerankTimeout = TimeSpan.FromSeconds(5);

    /// <summary>One answer waits at most this long for the chat model (design §8.3, O11).</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);
    /// <summary>An answer takes seconds, so under load it queues this long for a slot instead of failing at once (R6, O11).</summary>
    public static readonly TimeSpan AnswerSlotWait = TimeSpan.FromSeconds(10);
    /// <summary>Inline suggestions while typing: later than this they are no longer useful and simply do not appear (AI4-7). A local
    /// Gateway (IQ3_S, its prompt cache reusing the system part) takes 2–4 s for one, hence not the former 3 s.</summary>
    public static readonly TimeSpan EditorIntentBudget = TimeSpan.FromSeconds(5);
    /// <summary>The highest daily limit per user the admin may set (lower bound 1; null = no limit, the default, O10).</summary>
    public const int MaxDailyLimit = 10_000;

    public static readonly IReadOnlyList<AiTaskDefinition> All =
    [
        new(SearchEmbedQuery, AiProfileKind.Embeddings, AiInputClass.Query, "semanticSearch", "search", true, true),
        // Related pages are precomputed from the index; reading them never calls the model.
        new(PageRelated, AiProfileKind.Embeddings, AiInputClass.None, "related", "read", true, true),
        // E2 link candidates and E4 [[ ordering.
        new(EditorLink, AiProfileKind.Embeddings, AiInputClass.WorkingText, "editorLinks", "write", true, true),
        // P1–P3: the title of a page that does not exist yet.
        new(PageCreateSuggest, AiProfileKind.Embeddings, AiInputClass.WorkingText, "createSuggestions", "create", true, true),
        new(IndexEmbed, AiProfileKind.Embeddings, AiInputClass.Published, null, null, true, false),
        new(AdminTestEmbedding, AiProfileKind.Embeddings, AiInputClass.None, null, null, true, false),
        new(AdminEval, AiProfileKind.Chat, AiInputClass.None, null, null, true, false, Timeout: AnswerTimeout, SlotWait: AnswerSlotWait),
        new(AdminTestChat, AiProfileKind.Chat, AiInputClass.None, null, null, true, false),
        // Phase 3: the question (a query) and published chunks of readable pages included in AI, never working text, so a cloud
        // chat profile needs no further permission (Z2/D3). Both need an active generation for the chunk text (R3).
        new(SearchAnswer, AiProfileKind.Chat, AiInputClass.Published, "answer", "search", true, true, RequiresIndex: true,
            Timeout: AnswerTimeout, SlotWait: AnswerSlotWait, Limitable: true),
        new(ChatAnswer, AiProfileKind.Chat, AiInputClass.Published, "assistant", "read", true, true, RequiresIndex: true,
            Timeout: AnswerTimeout, SlotWait: AnswerSlotWait, Limitable: true),
        new(PageSummary, AiProfileKind.Chat, AiInputClass.Published, "pageSummary", "read", true, true, RequiresIndex: true,
            Timeout: AnswerTimeout, SlotWait: AnswerSlotWait, Limitable: true),
        new(EditorIntent, AiProfileKind.Quick, AiInputClass.WorkingText, "editorIntent", "write", false, true, Timeout: EditorIntentBudget, LatencyBudget: true),
        new(EditorTransform, AiProfileKind.Quick, AiInputClass.WorkingText, "editorTransform", "write", false, true, Timeout: AnswerTimeout, SlotWait: AnswerSlotWait),
        new(EditorOutline, AiProfileKind.Quick, AiInputClass.WorkingText, "editorOutline", "write", false, true, Timeout: AnswerTimeout, SlotWait: AnswerSlotWait),
        new(PublishCheck, AiProfileKind.Quick, AiInputClass.WorkingText, "publishCheck", "write", false, true, Timeout: AnswerTimeout, SlotWait: AnswerSlotWait),
        new(ChangeSummary, AiProfileKind.Quick, AiInputClass.WorkingText, "changeSummary", "write", false, true, Timeout: AnswerTimeout, SlotWait: AnswerSlotWait),
        new(TagSuggest, AiProfileKind.Quick, AiInputClass.WorkingText, "tagSuggestions", "write", false, true, Timeout: AnswerTimeout, SlotWait: AnswerSlotWait),
        // Agent turn (spec D3, §4.4): published content only, no index needed (lexical search works without one), limited per turn.
        new(AssistantAgent, AiProfileKind.Chat, AiInputClass.Published, "agent", "read", true, true,
            Timeout: AnswerTimeout, SlotWait: AnswerSlotWait, Limitable: true),
        // Calls no model: only the permission to read an author's working text in an agent turn (S1). Gives a cloud chat profile
        // the existing CloudWorkingText switch, so assistant.agent itself stays Published and available on cloud.
        new(AssistantWorkingText, AiProfileKind.Chat, AiInputClass.WorkingText, null, "write", true, true),
        // Calls no model either: the admin can take the batch-move tool out of the agent's offer (a smaller tool list is a faster,
        // surer turn on a small model); single moves stay. Capability arrangePages hides the task in the palette and the panel.
        new(AssistantArrange, AiProfileKind.Chat, AiInputClass.Published, "arrangePages", "write", true, true),
        // The question and published candidates the actor may read (never working text); no capability of its own: without it
        // answers keep today's sources.
        new(SearchRerank, AiProfileKind.Rerank, AiInputClass.Published, null, "search", true, true, Timeout: RerankTimeout),
        new(AdminTestRerank, AiProfileKind.Rerank, AiInputClass.None, null, null, true, false),
    ];

    public static AiTaskDefinition? Find(string? code)
        => All.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.Ordinal));
}
