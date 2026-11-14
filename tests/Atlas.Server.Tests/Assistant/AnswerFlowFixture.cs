using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Assistant.Answering;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Persistence;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>
/// The answer flow over the real index (plan phase 3 Q3): <see cref="AnswerSourcesFixture"/> (fake model and clock, an active
/// generation, a reader of the first space) plus a firemní LM Studio chat profile verified by a probe. The flow runs as the
/// endpoints run it: <see cref="AiAnswerRunner"/> in its own scope as the given actor, each step one Wolverine transaction.
/// </summary>
internal sealed class AnswerFlowFixture : IAsyncDisposable
{
    public const string ChatModel = "gemma-4-26b-a4b";

    private AnswerFlowFixture(AnswerSourcesFixture sources)
    {
        Sources = sources;
    }

    public AnswerSourcesFixture Sources { get; }
    public IndexFixture Index => Sources.Index;
    public UserAccount Reader => Sources.Reader;
    public UserAccount Writer => Sources.Writer;
    public UserAccount Admin => Sources.Admin;
    public FakeAiClientFactory Fake => Sources.Fake;
    public AiProfileView Chat { get; private set; } = null!;

    /// <param name="meaning">Which texts share a concept for the fake embeddings.</param>
    /// <param name="configure">Extra registrations (a second source adapter, a paused content access).</param>
    public static async Task<AnswerFlowFixture> CreateAsync(Func<string, string>? meaning = null, Action<IServiceCollection>? configure = null,
        int maxConcurrency = 2)
    {
        var f = new AnswerFlowFixture(await AnswerSourcesFixture.CreateAsync(meaning, configure));
        f.Chat = await f.Index.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, ChatProfile(maxConcurrency: maxConcurrency)));
        await f.Index.ProbeAsync();
        return f;
    }

    /// <summary>Firemní LM Studio with the chat profile's default timeout (120 s): the answer's own 30 s is shorter.</summary>
    public static SaveAiProfileRequest ChatProfile(string model = ChatModel, string? apiKey = null, string? expectedVersion = null, int maxConcurrency = 2)
        => new(true, AiProvider.OpenAiCompatible, AiLocation.Company, "http://lmstudio.example:1234/v1", model, null, null, null, null, 120,
            maxConcurrency, null, null, true, false, false, null, null, apiKey, false, expectedVersion);

    /// <summary>Saves a new chat revision and verifies it (a new revision is unavailable until probed).</summary>
    public async Task ChangeChatAsync(string model = ChatModel, string? apiKey = null)
    {
        Chat = await Index.As<AiProfileView>(Admin, new SaveAiProfile(AiProfileKind.Chat, ChatProfile(model, apiKey, Chat.Version)));
        await Index.ProbeAsync();
    }

    public async Task<T> RunAsync<T>(UserAccount actor, Func<AiAnswerRunner, Task<T>> action)
    {
        Index.Host.Actor.Current = actor;
        using var scope = Index.Host.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AiAnswerRunner>());
    }

    public Task<AiTurnPrepared> StartAsync(UserAccount actor, string question, AiScope? scope = null, string origin = AiConversationOrigin.Search)
        => RunAsync(actor, runner => runner.StartAsync(new StartAiConversationRequest(origin, question, scope ?? new AiScope("wiki", null, null)), default));

    public Task<AiTurnPrepared> FollowAsync(UserAccount actor, Guid conversationId, string question, AiScope? scope = null)
        => RunAsync(actor, runner => runner.AskAsync(conversationId, new AskAiRequest(question, scope ?? new AiScope("wiki", null, null)), default));

    public Task<AiMessageView> CompleteAsync(UserAccount actor, Guid messageId, CancellationToken ct = default)
        => RunAsync(actor, runner => runner.CompleteAsync(messageId, null, ct));

    /// <summary>Preparation and completion, as the web client runs one exchange.</summary>
    public async Task<(AiTurnPrepared Prepared, AiMessageView Answer)> AskAsync(UserAccount actor, string question, AiScope? scope = null,
        string origin = AiConversationOrigin.Search)
    {
        var prepared = await StartAsync(actor, question, scope, origin);
        Assert.Equal(AiMessageState.Preparing, prepared.State);
        return (prepared, await CompleteAsync(actor, prepared.MessageId));
    }

    /// <summary>The stored answer row and its dependencies, past every rule.</summary>
    public async Task<(AiMessageRow Message, IReadOnlyList<AiSourceDependencyRow> Dependencies)> StoredAsync(Guid messageId)
    {
        using var session = Index.Host.OpenSession();
        using var transaction = session.BeginTransaction();
        var store = new ConversationStore(session);
        var owner = await session.CreateSQLQuery("""
            SELECT c."OwnerId" FROM "AiMessage" m JOIN "AiConversation" c ON c."Id"=m."ConversationId" WHERE m."Id"=:id
            """).SetParameter("id", messageId).UniqueResultAsync<Guid>();
        var message = (await store.MessageAsync(messageId, owner, false, default))!.Message;
        var dependencies = (await store.DependenciesAsync([messageId], default)).GetValueOrDefault(messageId) ?? [];
        await transaction.CommitAsync();
        return (message, dependencies);
    }

    /// <summary>The chat calls the fake model received, in order.</summary>
    public IReadOnlyList<FakeAiCall> ChatCalls => Fake.Calls.Where(call => call.Kind == AiProfileKind.Chat).ToArray();

    public Task<AiCapabilitiesDto> CapabilitiesAsync() => Index.Host.Services.GetRequiredService<AiCapabilities>().GetAsync(default).AsTask();

    public Task<Guid> PublishAsync(string title, string markdown, Guid? spaceId = null, params (string Title, string Id)[] headings)
        => Sources.PublishAsync(title, markdown, spaceId, headings);

    public Task<int> IndexAsync() => Sources.IndexAsync();

    public Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters) => Sources.ExecuteAsync(sql, parameters);

    public async Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Index.Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>Whether any stored answer text of the schema contains <paramref name="text"/>.</summary>
    public async Task<bool> AnyStoredAnswerContainsAsync(string text)
        => await CountAsync("""SELECT count(*) FROM "AiMessage" WHERE strpos(CAST("Answer" AS text),@text)>0""", ("text", text)) > 0;

    public static async Task<ApiException> FailsAsync(int status, string? code, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal((status, code), (error.Status, error.Code));
        return error;
    }

    public ValueTask DisposeAsync() => Sources.DisposeAsync();
}
