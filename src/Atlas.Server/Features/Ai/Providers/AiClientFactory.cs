using System.ClientModel;
using System.ClientModel.Primitives;
using System.Security.Cryptography;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using OpenAI;
using Atlas.Server.Features.Ai.Pipeline;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Domain;

namespace Atlas.Server.Features.Ai.Providers;

/// <summary>
/// The production <see cref="IAiClientFactory"/>: OpenAI-compatible servers (LM Studio, OpenAI, Gateway) through <c>OpenAI</c> +
/// <c>Microsoft.Extensions.AI.OpenAI</c>, Claude through the official <c>Anthropic</c> SDK, both on the
/// <see cref="AiHttp.ClientName"/> HttpClient (no redirects) with SDK retries off (the caller, the index backoff and the circuit
/// breaker decide about retrying; a hidden retry would stretch the timeout and hide outages). The key is unprotected only
/// here, while the provider client is built, and lives only inside that client.
/// </summary>
public sealed partial class AiClientFactory(IHttpClientFactory http, IDataProtectionProvider protection, AiPipeline pipeline, TimeProvider clock,
    ILogger<AiClientFactory> logger) : IAiClientFactory
{
    /// <summary>The longest a probe waits, whatever the profile's timeout.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    /// <summary>The SDKs' own network timeout stays above the profile's, so the pipeline's timeout decides and is classified.</summary>
    private static readonly TimeSpan SdkTimeoutMargin = TimeSpan.FromSeconds(5);
    /// <summary>LM Studio without "Require authentication" ignores the key, but the OpenAI client needs a non-empty one.</summary>
    private const string KeylessPlaceholder = "lmstudio";
    /// <summary>Anthropic requires a maximum; 2.0 tasks answer briefly.</summary>
    private const int AnthropicMaxOutputTokens = 4096;
    private readonly IDataProtector m_protector = protection.CreateProtector(SaveAiProfileHandler.ApiKeyPurpose);

    public IChatClient Chat(AiProfileSnapshot profile, string task)
    {
        AiPipeline.RequireChat(profile, task);
        IChatClient provider;
        if (!TryReadKey(profile, out var key))
        {
            provider = new UnusableChatClient(AiOutcome.KeyUnreadable);
        }
        else if (profile.Provider == AiProvider.Anthropic)
        {
            provider = key is null ? new UnusableChatClient(AiOutcome.Auth) : Anthropic(profile, key).AsIChatClient(profile.Model, AnthropicMaxOutputTokens);
        }
        else
        {
            provider = OpenAi(profile, key).GetChatClient(profile.Model).AsIChatClient();
        }
        return pipeline.Chat(provider, profile, task);
    }

    public IEmbeddingGenerator<string, Embedding<float>> Embeddings(AiProfileSnapshot profile, string task)
    {
        AiPipeline.RequireEmbeddings(profile, task);
        IEmbeddingGenerator<string, Embedding<float>> provider = TryReadKey(profile, out var key)
            ? OpenAi(profile, key).GetEmbeddingClient(profile.Model).AsIEmbeddingGenerator()
            : new UnusableEmbeddingGenerator(AiOutcome.KeyUnreadable);
        return pipeline.Embeddings(provider, profile, task);
    }

    public IRerankClient Rerank(AiProfileSnapshot profile, string task)
    {
        AiPipeline.RequireRerank(profile, task);
        IRerankClient provider = TryReadKey(profile, out var key)
            ? new HttpRerankClient(http.CreateClient(AiHttp.ClientName), profile, key)
            : new UnusableRerankClient(AiOutcome.KeyUnreadable);
        return pipeline.Rerank(provider, profile, task);
    }

    public async Task<AiProbeResult> ProbeAsync(AiProfileSnapshot profile, CancellationToken ct)
    {
        if (!AiProvider.SupportsProfile(profile.Provider, profile.Location, profile.Kind))
        {
            return new AiProbeResult(false, AiOutcome.BadRequest, false);
        }
        if (!TryReadKey(profile, out var key))
        {
            return new AiProbeResult(false, AiOutcome.KeyUnreadable, false);
        }
        if (AiProvider.UsesOpenAiProtocol(profile.Provider) && profile.BaseUrl is null)
        {
            return new AiProbeResult(false, AiOutcome.BadRequest, false);
        }
        using var timeout = new CancellationTokenSource(profile.Timeout < ProbeTimeout ? profile.Timeout : ProbeTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await ProbeModelAsync(profile, key, linked.Token);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            var code = timeout.IsCancellationRequested && exception is OperationCanceledException ? AiOutcome.Timeout : AiErrorClassifier.Classify(exception, ct);
            logger.LogWarning("AI probe of the {Kind} profile at {Host} failed: {Outcome}", profile.Kind, profile.Host, code);
            return new AiProbeResult(false, code, false);
        }
    }

    private async Task<AiProbeResult> ProbeModelAsync(AiProfileSnapshot profile, string? key, CancellationToken ct)
    {
        if (profile.Provider == AiProvider.Anthropic)
        {
            if (key is null)
            {
                return new AiProbeResult(false, AiOutcome.Auth, false);
            }
            // The model's own entry: free, no content, and it resolves aliases the way the messages endpoint does.
            await Anthropic(profile, key).Models.Retrieve(profile.Model, new ModelRetrieveParams(), ct);
            return new AiProbeResult(true, null, true);
        }
        var models = await OpenAi(profile, key).GetOpenAIModelClient().GetModelsAsync(ct);
        return AiProbeResult.FromModelList(profile, models.Value.Select(model => model.Id).ToList());
    }

    /// <summary>The plain key, or null for a profile without one; false when the key ring can no longer unprotect it.</summary>
    private bool TryReadKey(AiProfileSnapshot profile, out string? key)
    {
        key = null;
        if (profile.ProtectedApiKey.Length == 0)
        {
            return true;
        }
        try
        {
            key = m_protector.Unprotect(profile.ProtectedApiKey);
            return true;
        }
        catch (CryptographicException)
        {
            logger.LogWarning("The API key of the {Kind} AI profile cannot be unprotected; enter it again in Administrace → AI", profile.Kind);
            return false;
        }
    }

    private OpenAIClient OpenAi(AiProfileSnapshot profile, string? key)
        => new(new ApiKeyCredential(key ?? KeylessPlaceholder), new OpenAIClientOptions
        {
            Endpoint = new Uri(profile.BaseUrl!),
            NetworkTimeout = profile.Timeout + SdkTimeoutMargin,
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            Transport = new HttpClientPipelineTransport(http.CreateClient(AiHttp.ClientName)),
        });

    /// <remarks>Every option is set explicitly: unset, the SDK reads ANTHROPIC_API_KEY, ANTHROPIC_AUTH_TOKEN and
    /// ANTHROPIC_BASE_URL from the environment, which would send another credential or reach another host. The SDK holds one
    /// credential: the API key alone stops the environment lookup, and setting AuthToken or Credentials too would replace it.</remarks>
    private AnthropicClient Anthropic(AiProfileSnapshot profile, string key)
        => new(new ClientOptions
        {
            ApiKey = key,
            BaseUrl = profile.BaseUrl ?? AiProfileSnapshot.AnthropicDefaultBaseUrl,
            HttpClient = http.CreateClient(AiHttp.ClientName),
            MaxRetries = 0,
            Timeout = profile.Timeout + SdkTimeoutMargin,
        });

    /// <summary>Stands in for a provider that cannot be built (unreadable or missing key): every call fails with the code and
    /// goes through the pipeline like any other failure, so usage and availability see it.</summary>
    private sealed class UnusableChatClient(string code) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new AiCallException(code);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new AiCallException(code);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class UnusableRerankClient(string code) : IRerankClient
    {
        public Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> documents, CancellationToken ct) => throw new AiCallException(code);
    }

    private sealed class UnusableEmbeddingGenerator(string code) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new AiCallException(code);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
