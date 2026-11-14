using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Atlas.Server.Features.Ai.Pipeline;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Domain;

namespace Atlas.Server.Tests.Ai.Fakes;

/// <summary>How the fake model of one profile kind behaves. The only model any automated test talks to (plan §0.7).</summary>
public abstract record FakeAiMode
{
    public static readonly FakeAiMode Healthy = new HealthyMode();
    /// <summary>Waits on the injected <see cref="TimeProvider"/> (a FakeTimeProvider never really waits), then answers.</summary>
    public static FakeAiMode Slow(TimeSpan delay) => new SlowMode(delay);
    /// <summary>Waits until the profile's timeout and throws <see cref="TaskCanceledException"/>, as HttpClient does.</summary>
    public static readonly FakeAiMode Timeout = new TimeoutMode();
    /// <summary>Fails the way an SDK would report the outcome: an HTTP status, a socket error, a parse error. The upstream text
    /// rides in the exception message, where the pipeline must never let it out.</summary>
    public static FakeAiMode Error(string code, string upstreamText = "upstream detail") => new ErrorMode(code, upstreamText);
    /// <summary>Chat answers without any JSON; embeddings come back with 768 dimensions.</summary>
    public static readonly FakeAiMode InvalidJson = new InvalidJsonMode();

    internal sealed record HealthyMode : FakeAiMode;
    internal sealed record SlowMode(TimeSpan Delay) : FakeAiMode;
    internal sealed record TimeoutMode : FakeAiMode;
    internal sealed record ErrorMode(string Code, string UpstreamText) : FakeAiMode;
    internal sealed record InvalidJsonMode : FakeAiMode;
}

/// <summary>One recorded call: the exact text that would have left Atlas, for "secrets and drafts never leave" tests.</summary>
public sealed record FakeAiCall(string Kind, string Task, string Operation, IReadOnlyList<string> Inputs, ChatOptions? ChatOptions,
    EmbeddingGenerationOptions? EmbeddingOptions);

/// <summary>A call of one kind that waits until the test releases it.</summary>
public sealed class FakeAiHold
{
    private readonly TaskCompletionSource m_entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource m_released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a call has reached the fake model.</summary>
    public Task Entered => m_entered.Task;

    public void Release() => m_released.TrySetResult();

    internal async Task WaitAsync(CancellationToken ct)
    {
        m_entered.TrySetResult();
        await m_released.Task.WaitAsync(ct);
    }
}

/// <summary>
/// The test double of <see cref="IAiClientFactory"/>: deterministic provider clients behind the production pipeline
/// (<see cref="AiPipeline"/>), so usage, defaults, the schema fallback, normalization, timeouts and the outcome sink are the
/// real ones. A mode per profile kind, recorded inputs, call and probe counts and the highest concurrency seen.
/// </summary>
public sealed class FakeAiClientFactory : IAiClientFactory
{
    public const string ChatModel = "fake-chat";
    public const string EmbeddingModel = "fake-trigram-1024";
    /// <summary>The model the server lists instead of the profile's after <see cref="SetModelListed"/> false.</summary>
    public const string OtherModel = "fake-other-model";
    private readonly AiPipeline m_pipeline;
    private readonly TimeProvider m_clock;
    private readonly ConcurrentDictionary<string, FakeAiMode> m_modes = new();
    private readonly ConcurrentDictionary<string, FakeAiHold> m_holds = new();
    private readonly ConcurrentDictionary<string, bool> m_unlisted = new();
    private readonly ConcurrentDictionary<string, int> m_probes = new();
    private readonly ConcurrentQueue<Guid> m_probedRevisions = new();
    private readonly ConcurrentDictionary<string, int[]> m_concurrency = new();
    private readonly ConcurrentQueue<FakeAiCall> m_calls = new();

    public FakeAiClientFactory(AiPipeline pipeline, TimeProvider clock)
    {
        m_pipeline = pipeline;
        m_clock = clock;
    }

    /// <summary>The chat answer. The default answers <c>{"ok":true}</c> to any JSON request (natively or with the schema in
    /// the prompt, then wrapped in prose as weaker models do) and "připraveno" otherwise.</summary>
    public Func<FakeAiCall, string> ChatAnswer { get; set; } = DefaultAnswer;

    /// <summary>Near vectors for retrieval tests: the text an input is embedded as (by default the input itself). A test maps a
    /// paraphrase and the passage it should find to one concept (identical vectors) or to <see cref="FakeAi.Near"/> of it, so a
    /// page is found by meaning without sharing a word with the query. The recorded inputs stay what left Atlas.</summary>
    public Func<string, string> Meaning { get; set; } = text => text;

    /// <summary>A scripted chat turn (tool calls, then a text built from the tool results); null falls back to <see cref="ChatAnswer"/>.</summary>
    public Func<IReadOnlyList<ChatMessage>, ChatMessage?>? Script { get; set; }

    public IReadOnlyList<FakeAiCall> Calls => m_calls.ToArray();

    public void SetMode(string kind, FakeAiMode mode) => m_modes[kind] = mode;

    /// <summary>The probe's server lists <see cref="OtherModel"/> instead of the profile's model.</summary>
    public void SetModelListed(string kind, bool listed) => m_unlisted[kind] = !listed;

    /// <summary>Calls of the kind wait until <see cref="FakeAiHold.Release"/>; the hold ends with the release.</summary>
    public FakeAiHold Hold(string kind) => m_holds[kind] = new FakeAiHold();

    public int Probes(string kind) => m_probes.GetValueOrDefault(kind);

    /// <summary>The revision of every probed profile, in order: a probe never carries content, only the profile.</summary>
    public IReadOnlyList<Guid> ProbedRevisions => m_probedRevisions.ToArray();

    public int MaxConcurrent(string kind) => m_concurrency.TryGetValue(kind, out var counters) ? Volatile.Read(ref counters[1]) : 0;

    public IChatClient Chat(AiProfileSnapshot profile, string task)
    {
        AiPipeline.RequireChat(profile, task);
        return m_pipeline.Chat(new FakeChatClient(this, profile, task), profile, task);
    }

    public IEmbeddingGenerator<string, Embedding<float>> Embeddings(AiProfileSnapshot profile, string task)
    {
        AiPipeline.RequireEmbeddings(profile, task);
        return m_pipeline.Embeddings(new FakeEmbeddingGenerator(this, profile, task), profile, task);
    }

    /// <summary>The rerank scores; null: the cosine of the query's and each document's fake vector (<see cref="Meaning"/> applied).</summary>
    public Func<string, IReadOnlyList<string>, IReadOnlyList<double>>? RerankScores { get; set; }

    public IRerankClient Rerank(AiProfileSnapshot profile, string task)
    {
        AiPipeline.RequireRerank(profile, task);
        return m_pipeline.Rerank(new FakeRerankClient(this, profile, task), profile, task);
    }

    public Task<AiModelCatalogue> ListModelsAsync(AiProfileSnapshot connection, CancellationToken ct)
        => Task.FromResult(new AiModelCatalogue(true, null, [new(EmbeddingModel, EmbeddingModel), new(ChatModel, ChatModel)]));

    public async Task<AiProbeResult> ProbeAsync(AiProfileSnapshot profile, CancellationToken ct)
    {
        m_probes.AddOrUpdate(profile.Kind, 1, (_, count) => count + 1);
        m_probedRevisions.Enqueue(profile.RevisionId);
        var listed = !m_unlisted.GetValueOrDefault(profile.Kind);
        switch (Mode(profile.Kind))
        {
            case FakeAiMode.SlowMode slow:
                await Task.Delay(slow.Delay, m_clock, ct);
                break;
            case FakeAiMode.TimeoutMode:
                return new AiProbeResult(false, AiOutcome.Timeout, false);
            case FakeAiMode.ErrorMode error:
                return new AiProbeResult(false, error.Code, false);
        }
        return AiProbeResult.FromModelList(profile, [listed ? profile.Model : OtherModel]);
    }

    private FakeAiMode Mode(string kind) => m_modes.GetValueOrDefault(kind, FakeAiMode.Healthy);

    /// <summary>Records the call, then behaves as the kind's mode says. Returns false for <see cref="FakeAiMode.InvalidJson"/>.</summary>
    private async Task<bool> EnterAsync(FakeAiCall call, AiProfileSnapshot profile, CancellationToken ct)
    {
        m_calls.Enqueue(call);
        var counters = m_concurrency.GetOrAdd(profile.Kind, _ => new int[2]);
        var current = Interlocked.Increment(ref counters[0]);
        try
        {
            int seen;
            while (current > (seen = Volatile.Read(ref counters[1])) && Interlocked.CompareExchange(ref counters[1], current, seen) != seen)
            {
            }
            if (m_holds.TryGetValue(profile.Kind, out var hold))
            {
                await hold.WaitAsync(ct);
                m_holds.TryRemove(KeyValuePair.Create(profile.Kind, hold));
            }
            switch (Mode(profile.Kind))
            {
                case FakeAiMode.SlowMode slow:
                    await Task.Delay(slow.Delay, m_clock, ct);
                    return true;
                case FakeAiMode.TimeoutMode:
                    await Task.Delay(profile.Timeout, m_clock, ct);
                    throw new TaskCanceledException($"The request was canceled due to the configured HttpClient.Timeout of {profile.TimeoutSeconds} seconds elapsing.");
                case FakeAiMode.ErrorMode error:
                    throw Failure(error);
                case FakeAiMode.InvalidJsonMode:
                    return false;
                default:
                    return true;
            }
        }
        finally
        {
            Interlocked.Decrement(ref counters[0]);
        }
    }

    private static Exception Failure(FakeAiMode.ErrorMode error) => error.Code switch
    {
        AiOutcome.Auth => new HttpRequestException(error.UpstreamText, null, HttpStatusCode.Unauthorized),
        AiOutcome.ModelNotFound => new HttpRequestException(error.UpstreamText, null, HttpStatusCode.NotFound),
        AiOutcome.Http429 => new HttpRequestException(error.UpstreamText, null, HttpStatusCode.TooManyRequests),
        AiOutcome.Http5xx => new HttpRequestException(error.UpstreamText, null, HttpStatusCode.ServiceUnavailable),
        AiOutcome.BadRequest => new HttpRequestException(error.UpstreamText, null, HttpStatusCode.BadRequest),
        AiOutcome.Connection => new HttpRequestException(error.UpstreamText, new SocketException((int)SocketError.ConnectionRefused)),
        AiOutcome.InvalidOutput => new JsonException(error.UpstreamText),
        AiOutcome.RedirectRefused => new HttpRequestException(error.UpstreamText, new AiRedirectRefusedException("fake.example", 307)),
        AiOutcome.KeyUnreadable => new AiCallException(AiOutcome.KeyUnreadable),
        _ => throw new ArgumentOutOfRangeException(nameof(error), error.Code, "The fake has no failure for this outcome."),
    };

    private static string DefaultAnswer(FakeAiCall call)
    {
        if (call.ChatOptions?.ResponseFormat is ChatResponseFormatJson)
        {
            return """{"ok":true}""";
        }
        return call.Inputs.Any(text => text.Contains(PromptSchemaChatClient.SchemaInstruction, StringComparison.Ordinal))
            ? "Tady je výsledek:\n```json\n{\"ok\":true}\n```\nHotovo."
            : "připraveno";
    }

    private sealed class FakeChatClient(FakeAiClientFactory owner, AiProfileSnapshot profile, string task) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            var call = new FakeAiCall(profile.Kind, task, "chat", list.Select(message => message.Text).ToArray(), options?.Clone(), null);
            var entered = await owner.EnterAsync(call, profile, cancellationToken);
            if (entered && owner.Script?.Invoke(list) is { } scripted)
            {
                return new ChatResponse(scripted) { ModelId = ChatModel, FinishReason = ChatFinishReason.Stop };
            }
            var text = entered ? owner.ChatAnswer(call) : "Omlouvám se, odpověď nemám.";
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                ModelId = ChatModel,
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = list.Sum(message => message.Text.Length) / 4 + 1, OutputTokenCount = text.Length / 4 + 1 },
            };
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeRerankClient(FakeAiClientFactory owner, AiProfileSnapshot profile, string task) : IRerankClient
    {
        public async Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> documents, CancellationToken ct)
        {
            var valid = await owner.EnterAsync(new FakeAiCall(profile.Kind, task, "rerank", [query, .. documents], null, null), profile, ct);
            if (!valid)
            {
                return [];
            }
            var meaning = FakeAi.Vector(owner.Meaning(query));
            return owner.RerankScores?.Invoke(query, documents)
                ?? documents.Select(document => FakeAi.Cosine(meaning, FakeAi.Vector(owner.Meaning(document)))).ToArray();
        }
    }

    private sealed class FakeEmbeddingGenerator(FakeAiClientFactory owner, AiProfileSnapshot profile, string task) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var texts = values.ToArray();
            var call = new FakeAiCall(profile.Kind, task, "embedding", texts, null, options?.Clone());
            var valid = await owner.EnterAsync(call, profile, cancellationToken);
            // Raw trigram counts: the pipeline, not the fake, normalizes. Token usage is unknown, as with LM Studio.
            return new GeneratedEmbeddings<Embedding<float>>(texts.Select(text => new Embedding<float>(valid ? FakeAi.Counts(owner.Meaning(text)) : new float[768])
            {
                ModelId = EmbeddingModel,
            }));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}

/// <summary>One <c>&lt;zdroj&gt;</c> block of an answer prompt as the fake model reads it (entities decoded).</summary>
public sealed record FakePromptSource(int Number, string Page, string Section, string Text);

public static class FakeAi
{
    public const int Dimensions = 1024;

    /// <summary>
    /// Chat answers of the answer flow (plan phase 3 §7): set one as <see cref="FakeAiClientFactory.ChatAnswer"/>. Each reads the
    /// <c>&lt;zdroj n=…&gt;</c> blocks of the last user message and answers the <c>AnswerResult</c> JSON the production prompt asks
    /// for (camelCase, as the structured output reads it).
    /// </summary>
    public static class Answers
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>Any answer computed from the passed sources.</summary>
        public static Func<FakeAiCall, string> From(Func<IReadOnlyList<FakePromptSource>, Atlas.Server.Features.Assistant.Answering.AnswerResult> answer)
            => call => JsonSerializer.Serialize(answer(Sources(call)), Json);

        /// <summary>The given sentences with their source numbers.</summary>
        public static Func<FakeAiCall, string> Answerable(params (string Text, int[] Sources)[] sentences)
            => From(_ => new(true, sentences.Select(x => new Atlas.Server.Features.Assistant.Answering.AnswerSentence(x.Text, x.Sources)).ToArray()));

        /// <summary>The model admits that the sources do not answer.</summary>
        public static Func<FakeAiCall, string> Unanswerable { get; } = From(_ => new(false, []));

        /// <summary>One sentence citing <paramref name="number"/>, whether or not such a source was passed.</summary>
        public static Func<FakeAiCall, string> Citing(int number) => Answerable(("Podle zdroje je to takto.", [number]));

        /// <summary>One sentence that writes its source numbers into the text as well (§15: models do).</summary>
        public static Func<FakeAiCall, string> WithNumbersInText { get; } = Answerable(("Postup je popsaný v návodu [1] a v příručce (zdroj 1).", [1]));

        /// <summary>The text of source <paramref name="number"/> as the one sentence, citing it.</summary>
        public static Func<FakeAiCall, string> EchoingSource(int number)
            => From(sources => new(true, [new(sources.Single(source => source.Number == number).Text, [number])]));

        /// <summary>The source blocks of the call's last user message.</summary>
        public static IReadOnlyList<FakePromptSource> Sources(FakeAiCall call)
        {
            var user = call.Inputs.Count == 0 ? "" : call.Inputs[^1];
            return System.Text.RegularExpressions.Regex.Matches(user,
                    "<zdroj n=\"(\\d+)\" stránka=\"([^\"]*)\" sekce=\"([^\"]*)\">\\r?\\n(.*?)\\r?\\n</zdroj>",
                    System.Text.RegularExpressions.RegexOptions.Singleline)
                .Select(match => new FakePromptSource(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), Decode(match.Groups[2].Value),
                    Decode(match.Groups[3].Value), Decode(match.Groups[4].Value)))
                .ToArray();
        }

        private static string Decode(string text) => text.Replace("&quot;", "\"").Replace("&lt;", "<").Replace("&gt;", ">");
    }

    /// <summary>Replaces the real factory: every host of the test suite registers this, so no test can reach a real model.</summary>
    public static void Register(IServiceCollection services)
    {
        services.TryAddSingleton<FakeAiClientFactory>();
        services.Replace(ServiceDescriptor.Singleton<IAiClientFactory>(provider => provider.GetRequiredService<FakeAiClientFactory>()));
    }

    /// <summary>A profile snapshot as the configuration would hold it; firemní LM Studio unless changed.</summary>
    public static AiProfileSnapshot Profile(string kind, Guid? revisionId = null) => new(
        revisionId ?? Guid.NewGuid(), kind, true, AiProvider.OpenAiCompatible, AiLocation.Company, "http://lmstudio.example:1234/v1",
        kind == AiProfileKind.Embeddings ? "text-embedding-bge-m3" : "gemma-4-26b-a4b", null, 30, 4, null, null, true,
        kind == AiProfileKind.Embeddings ? Dimensions : null, "", "", false, false, null, null, null);

    /// <summary>The unit vector the pipeline returns for the text: hashed character trigrams of its normalized words.</summary>
    public static float[] Vector(string text)
    {
        var vector = Counts(text);
        var norm = MathF.Sqrt(vector.Sum(x => x * x));
        return norm == 0 ? vector : vector.Select(x => x / norm).ToArray();
    }

    /// <summary>A different text whose vector is close to the text's (cosine well above 0.8), so ranking tests do not depend
    /// on exact equality.</summary>
    public static string Near(string text)
    {
        var words = Words(text).ToArray();
        return words.Length == 0 ? text : text + " " + words.OrderByDescending(word => word.Length).First();
    }

    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    internal static float[] Counts(string text)
    {
        var vector = new float[Dimensions];
        foreach (var word in Words(text))
        {
            var padded = $" {word} ";
            for (var i = 0; i + 3 <= padded.Length; i++)
            {
                vector[Fnv(padded.AsSpan(i, 3)) % Dimensions] += 1;
            }
        }
        return vector;
    }

    private static IEnumerable<string> Words(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                plain.Append(char.IsLetterOrDigit(c) ? c : ' ');
            }
        }
        return plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>FNV-1a: stable across processes, unlike string.GetHashCode.</summary>
    private static uint Fnv(ReadOnlySpan<char> chars)
    {
        var hash = 2166136261u;
        foreach (var c in chars)
        {
            hash = (hash ^ c) * 16777619u;
        }
        return hash;
    }
}
