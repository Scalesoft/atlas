using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Ai.Pipeline;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Usage;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Ai;

/// <summary>A2: the delegating MEAI clients around every provider, exercised through the fake provider and a fake clock.</summary>
public sealed class AiPipelineTests
{
    private const string Secret = "TAJNE-123";

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Usage_records_task_profile_model_tokens_and_latency_but_never_prompt_or_answer_text()
    {
        using var ai = new PipelineHost();
        var chatProfile = FakeAi.Profile(AiProfileKind.Chat);
        ai.Fake.ChatAnswer = _ => $"Odpověď s {Secret}.";
        using var chat = ai.Fake.Chat(chatProfile, AiTaskCatalog.AdminTestChat);
        var hold = ai.Fake.Hold(AiProfileKind.Chat);
        var pending = chat.GetResponseAsync($"Dotaz s {Secret}");
        await hold.Entered;
        ai.Clock.Advance(TimeSpan.FromMilliseconds(250));
        hold.Release();
        Assert.Contains(Secret, (await pending).Text, StringComparison.Ordinal);

        var embeddingProfile = FakeAi.Profile(AiProfileKind.Embeddings);
        using var embeddings = ai.Fake.Embeddings(embeddingProfile, AiTaskCatalog.IndexEmbed);
        await embeddings.GenerateAsync([$"Kapitola {Secret}", "druhá kapitola"]);

        ai.Fake.SetMode(AiProfileKind.Chat, FakeAiMode.Error(AiOutcome.Http5xx, $"upstream says {Secret}"));
        var failure = await Assert.ThrowsAsync<AiCallException>(() => chat.GetResponseAsync($"Další {Secret}"));
        Assert.Equal(AiOutcome.Http5xx, failure.Code);
        Assert.DoesNotContain(Secret, failure.ToString(), StringComparison.Ordinal);

        var usage = ai.Usage.Drain();
        Assert.Equal(3, usage.Count);
        var answered = usage[0];
        Assert.Equal((AiTaskCatalog.AdminTestChat, AiProfileKind.Chat, "chat", chatProfile.RevisionId, FakeAiClientFactory.ChatModel, AiOutcome.Ok),
            (answered.Task, answered.Kind, answered.Operation, answered.ProfileRevisionId, answered.Model, answered.Outcome));
        Assert.Equal(250, answered.LatencyMs);
        Assert.True(answered.InputTokens > 0 && answered.OutputTokens > 0);
        Assert.Null(answered.Items);
        var embedded = usage[1];
        Assert.Equal((AiTaskCatalog.IndexEmbed, "embedding", 2, FakeAiClientFactory.EmbeddingModel, AiOutcome.Ok),
            (embedded.Task, embedded.Operation, embedded.Items, embedded.Model, embedded.Outcome));
        // The fake, like LM Studio, reports no token counts: unknown stays null, never zero.
        Assert.Null(embedded.InputTokens);
        Assert.Null(embedded.OutputTokens);
        Assert.Equal((AiOutcome.Http5xx, chatProfile.Model, (long?)null), (usage[2].Outcome, usage[2].Model, usage[2].InputTokens));

        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(usage), StringComparison.Ordinal);
        Assert.NotEmpty(ai.Logs.Messages);
        Assert.DoesNotContain(ai.Logs.Messages, line => line.Contains(Secret, StringComparison.Ordinal));
        // The outcome sink (A3's circuit breaker) heard of both calls and their codes, nothing else.
        Assert.Equal([(chatProfile.RevisionId, AiOutcome.Ok), (embeddingProfile.RevisionId, AiOutcome.Ok), (chatProfile.RevisionId, AiOutcome.Http5xx)],
            ai.Outcomes.Reported);
    }

    [Fact]
    public async Task Profile_defaults_set_reasoning_effort_none_and_temperature_only_when_the_caller_did_not()
    {
        using var ai = new PipelineHost();
        using var plain = ai.Fake.Chat(FakeAi.Profile(AiProfileKind.Chat), AiTaskCatalog.AdminTestChat);
        await plain.GetResponseAsync("Ahoj");
        using var tuned = ai.Fake.Chat(FakeAi.Profile(AiProfileKind.Chat) with { ReasoningEffort = "high", Temperature = 0.3f }, AiTaskCatalog.AdminTestChat);
        await tuned.GetResponseAsync("Ahoj");
        await tuned.GetResponseAsync("Ahoj", new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low }, Temperature = 0.9f });

        var options = ai.Fake.Calls.Select(call => call.ChatOptions!).ToArray();
        // Unset profile: reasoning off (§15, every 2.0 task), the provider's default temperature.
        Assert.Equal((ReasoningEffort.None, (float?)null), (options[0].Reasoning?.Effort, options[0].Temperature));
        Assert.Equal((ReasoningEffort.High, (float?)0.3f), (options[1].Reasoning?.Effort, options[1].Temperature));
        Assert.Equal((ReasoningEffort.Low, (float?)0.9f), (options[2].Reasoning?.Effort, options[2].Temperature));
    }

    /// <summary>Nothing streams a chat answer, so no decorator handles a stream; one that reached the provider through a plain
    /// DelegatingChatClient would skip the gate, usage recording, the defaults, the schema fallback and the availability report.</summary>
    [Fact]
    public void A_stream_through_the_pipeline_fails_instead_of_bypassing_the_decorators()
    {
        using var ai = new PipelineHost();
        using var chat = ai.Fake.Chat(FakeAi.Profile(AiProfileKind.Chat), AiTaskCatalog.AdminTestChat);
        Assert.Throws<NotSupportedException>(() => chat.GetStreamingResponseAsync("Ahoj"));
        Assert.Empty(ai.Fake.Calls);
        Assert.Empty(ai.Usage.Drain());
    }

    [Fact]
    public async Task Without_native_json_schema_the_schema_moves_to_the_system_prompt_and_json_is_cut_from_text()
    {
        using var ai = new PipelineHost();
        var fallback = FakeAi.Profile(AiProfileKind.Chat) with { NativeJsonSchema = false };
        using var chat = ai.Fake.Chat(fallback, AiTaskCatalog.AdminTestChat);
        var answer = await chat.GetResponseAsync<Answer>([new ChatMessage(ChatRole.System, "Jsi test."), new ChatMessage(ChatRole.User, "Potvrď.")],
            JsonSerializerOptions.Web);
        Assert.True(answer.Result.Ok);
        var sent = ai.Fake.Calls[^1];
        // No response_format reaches a model that breaks on grammar sampling; the schema is one system message instead.
        Assert.Null(sent.ChatOptions!.ResponseFormat);
        var system = Assert.Single(sent.Inputs, text => text.StartsWith("Jsi test.", StringComparison.Ordinal));
        Assert.Contains(PromptSchemaChatClient.SchemaInstruction, system, StringComparison.Ordinal);
        Assert.Contains("\"ok\"", system, StringComparison.Ordinal);

        // A native profile sends the schema as response_format and leaves the prompt alone.
        using var native = ai.Fake.Chat(FakeAi.Profile(AiProfileKind.Chat), AiTaskCatalog.AdminTestChat);
        Assert.True((await native.GetResponseAsync<Answer>("Potvrď.", JsonSerializerOptions.Web)).Result.Ok);
        Assert.IsType<ChatResponseFormatJson>(ai.Fake.Calls[^1].ChatOptions!.ResponseFormat);
        Assert.DoesNotContain(ai.Fake.Calls[^1].Inputs, text => text.Contains(PromptSchemaChatClient.SchemaInstruction, StringComparison.Ordinal));

        ai.Fake.SetMode(AiProfileKind.Chat, FakeAiMode.InvalidJson);
        var invalid = await Assert.ThrowsAsync<AiInvalidOutputException>(() => chat.GetResponseAsync<Answer>("Potvrď.", JsonSerializerOptions.Web));
        Assert.Equal(AiOutcome.InvalidOutput, invalid.Code);
        Assert.Equal([AiOutcome.Ok, AiOutcome.Ok, AiOutcome.InvalidOutput], ai.Usage.Drain().Select(x => x.Outcome));
    }

    [Fact]
    public async Task Embeddings_are_normalized_and_a_wrong_dimension_is_an_invalid_output()
    {
        using var ai = new PipelineHost();
        var profile = FakeAi.Profile(AiProfileKind.Embeddings) with { RequestDimensions = true };
        using var generator = ai.Fake.Embeddings(profile, AiTaskCatalog.SearchEmbedQuery);
        var result = await generator.GenerateAsync(["připojení k VPN z domova", FakeAi.Near("připojení k VPN z domova")]);
        Assert.All(result, embedding =>
        {
            Assert.Equal(FakeAi.Dimensions, embedding.Vector.Length);
            Assert.Equal(1.0, Math.Sqrt(embedding.Vector.ToArray().Sum(x => (double)x * x)), 5);
        });
        Assert.Equal(FakeAi.Vector("připojení k VPN z domova"), result[0].Vector.ToArray());
        Assert.InRange(FakeAi.Cosine(result[0].Vector.Span, result[1].Vector.Span), 0.8, 0.9999);
        // RequestDimensions asks an OpenAI model for 1 024 dimensions.
        Assert.Equal(FakeAi.Dimensions, ai.Fake.Calls[^1].EmbeddingOptions?.Dimensions);

        ai.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.InvalidJson);
        var invalid = await Assert.ThrowsAsync<AiInvalidOutputException>(() => generator.GenerateAsync(["jiný text"]));
        Assert.Equal(AiOutcome.InvalidOutput, invalid.Code);
        Assert.Equal([AiOutcome.Ok, AiOutcome.InvalidOutput], ai.Usage.Drain().Select(x => x.Outcome));
    }

    private sealed record Answer(bool Ok);

    /// <summary>The real AddAi registrations without a database: fake provider, fake clock, captured logs, recording sink.</summary>
    internal sealed class PipelineHost : IDisposable
    {
        private readonly ServiceProvider m_services;

        public PipelineHost()
        {
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
            services.AddSingleton<TimeProvider>(Clock);
            services.AddAi();
            services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
            services.AddSingleton<IAiOutcomeSink>(Outcomes);
            FakeAi.Register(services);
            m_services = services.BuildServiceProvider();
        }

        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
        public CaptureLoggerProvider Logs { get; } = new();
        public RecordingOutcomes Outcomes { get; } = new();
        public IServiceProvider Services => m_services;
        public FakeAiClientFactory Fake => m_services.GetRequiredService<FakeAiClientFactory>();
        public AiUsageRecorder Usage => m_services.GetRequiredService<AiUsageRecorder>();

        public void Dispose() => m_services.Dispose();
    }

    internal sealed class RecordingOutcomes : IAiOutcomeSink
    {
        private readonly List<(Guid, string)> m_reported = [];

        public IReadOnlyList<(Guid RevisionId, string Code)> Reported
        {
            get
            {
                lock (m_reported)
                {
                    return m_reported.ToArray();
                }
            }
        }

        public void Success(AiProfileSnapshot profile)
        {
            lock (m_reported)
            {
                m_reported.Add((profile.RevisionId, AiOutcome.Ok));
            }
        }

        public void Failure(AiProfileSnapshot profile, string code)
        {
            lock (m_reported)
            {
                m_reported.Add((profile.RevisionId, code));
            }
        }
    }
}
