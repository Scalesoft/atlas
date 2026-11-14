using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Persistence;
using Atlas.Server.Features.Assistant.Sources;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.Assistant;

/// <summary>Conversations as the answer flow (Q3) leaves them, written through the store on any test host: a pinned chat
/// revision, a conversation, and answered turns with their dependencies. The reads under test (Q4) go through Wolverine.</summary>
internal static class ConversationSeeds
{
    /// <summary>A generation no index has: page dependencies on it are available by status (page and revision), but their chunk
    /// text is gone, so they have no excerpt and no heading title.</summary>
    public static readonly Guid MissingGeneration = Guid.NewGuid();

    public const string Model = "gemma-4";

    public static AiSourceDependencyRow Source(int ordinal, (Guid PageId, Guid RevisionId) page, string kind = AiDependencyKind.Context)
        => AiSourceDependencyRow.Page(ordinal, kind, page.PageId, page.RevisionId, MissingGeneration, ordinal, "sekce-" + ordinal);

    public static AiStoredAnswer Answer(params (string Text, int[] Sources)[] sentences)
        => new(true, sentences.Select(x => new AiStoredSentence(x.Text, x.Sources)).ToArray());

    /// <summary>A chat revision of Firemní LM Studio as A1 stores it, not assigned: only something to pin.</summary>
    public static async Task<(Guid Id, string Fingerprint)> SeedChatRevisionAsync(AtlasTestHost host, UserAccount admin)
    {
        var id = Guid.NewGuid();
        await host.SeedAsync(session => session.CreateSQLQuery("""
            INSERT INTO "AiProfileRevision"("Id","Kind","Enabled","Provider","Location","BaseUrl","Model","TimeoutSeconds","MaxConcurrency","CreatedById","CreatedAt")
            VALUES (:id,'chat',true,'openai-compatible','company','http://lmstudio.example:1234/v1',:model,120,2,:admin,now())
            """).SetParameter("id", id).SetParameter("model", Model).SetParameter("admin", admin.Id).ExecuteUpdateAsync());
        return (id, ChatProfileCompatibility.Fingerprint("openai-compatible", "company", "http://lmstudio.example:1234/v1", Model));
    }

    public static async Task<T> StoreAsync<T>(AtlasTestHost host, Func<ConversationStore, Task<T>> action)
    {
        using var session = host.OpenSession();
        using var transaction = session.BeginTransaction();
        var result = await action(new ConversationStore(session));
        await transaction.CommitAsync();
        return result;
    }

    public static async Task<AiConversationRow> StartAsync(AtlasTestHost host, UserAccount owner, (Guid Id, string Fingerprint) chat,
        string origin = AiConversationOrigin.Assistant, DateTime? at = null)
    {
        var now = at ?? ConversationFixture.Now();
        var conversation = new AiConversationRow(Guid.NewGuid(), owner.Id, origin, origin == AiConversationOrigin.Assistant, chat.Id, chat.Fingerprint,
            now, now, 0);
        await StoreAsync(host, async store =>
        {
            await store.InsertConversationAsync(conversation, default);
            return true;
        });
        return conversation;
    }

    /// <summary>A question and its answer released by the gate: the given sentences, the context sources they cite marked.</summary>
    public static Task<(AiMessageRow Question, AiMessageRow Answer)> AnsweredAsync(AtlasTestHost host, AiConversationRow conversation, string question,
        IReadOnlyList<AiSourceDependencyRow> dependencies, AiStoredAnswer answer, DateTime? at = null) => StoreAsync(host, async store =>
    {
        var now = at ?? ConversationFixture.Now();
        var ordinal = await store.NextOrdinalAsync(conversation.Id, default);
        var user = AiMessageRow.UserQuestion(Guid.NewGuid(), conversation.Id, ordinal, question, AiMessageScope.Wiki, now);
        var prepared = AiMessageRow.PreparedAnswer(Guid.NewGuid(), conversation.Id, ordinal + 1, AiMessageState.Preparing, "chat.answer",
            AnswerRetrieval.Lexical, "answer@1", conversation.ChatProfileRevisionId, Model, now);
        await store.InsertTurnAsync(user, prepared, dependencies, default);
        var cited = answer.Sentences.SelectMany(x => x.Sources).Distinct().ToArray();
        if (!await store.CompleteAsync(prepared.Id, new AiAnswerCompletion(AiMessageState.Answered, answer, null, now, cited, InputTokens: 900,
            OutputTokens: 40, LatencyMs: 1200), default))
        {
            throw new InvalidOperationException("The answer was not preparing.");
        }
        return (user, prepared);
    });

    public static Task<T> As<T>(this AtlasTestHost host, UserAccount actor, object message)
    {
        host.Actor.Current = actor;
        return host.InvokeAsync<T>(message);
    }

    public static Task As(this AtlasTestHost host, UserAccount actor, object message)
    {
        host.Actor.Current = actor;
        return host.InvokeAsync(message);
    }
}
