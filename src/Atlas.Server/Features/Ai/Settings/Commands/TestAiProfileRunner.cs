using System.Text.Json;
using Microsoft.Extensions.AI;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Wolverine;

namespace Atlas.Server.Features.Ai.Settings.Commands;

/// <summary>
/// "Test spojení" (<c>POST /api/admin/ai/profiles/{kind}/test</c>): always against the saved revision, through the real
/// pipeline (so the call is usage under <c>admin.test-*</c> and its outcome reaches availability), with no transaction open
/// during the call. Embeddings: one synthetic sentence must come back as a finite 1 024-dimension vector. Chat and quick tasks:
/// structured output with a tiny schema, which also proves the <c>NativeJsonSchema</c> fallback. Rerank: two synthetic documents, the
/// matching one must score higher. The answer holds codes only.
/// A test runs whatever the profile's availability, because it is how an admin diagnoses an unavailable one.
/// </summary>
public sealed class TestAiProfileRunner(IMessageBus bus, IAiClientFactory factory, TimeProvider clock)
{
    /// <summary>Synthetic, so a test never sends wiki content.</summary>
    public const string ProbeSentence = "Atlas test spojení: připojení k síti VPN";
    private const int MaxModelLength = 200;

    public async Task<AiTestResult> TestAsync(string kind, CancellationToken ct)
    {
        var profile = await bus.InvokeAsync<AiProfileSnapshot>(new BeginAiProfileTest(kind), ct);
        var result = await TestSnapshotAsync(profile, ct);
        await bus.InvokeAsync(new CompleteAiProfileTest(profile.Kind, profile.RevisionId), ct);
        return result;
    }

    /// <summary>The same synthetic probe for either the active assignment or an explicitly selected library revision.</summary>
    public async Task<AiTestResult> TestSnapshotAsync(AiProfileSnapshot profile, CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        AiTestResult result;
        try
        {
            result = profile.Kind switch
            {
                AiProfileKind.Embeddings => await EmbeddingsAsync(profile, started, ct),
                AiProfileKind.Rerank => await RerankAsync(profile, started, ct),
                _ => await ChatAsync(profile, started, ct),
            };
        }
        catch (AiCallException exception)
        {
            result = new AiTestResult(false, exception.Code, Latency(started), null, null);
        }
        catch (AiConfigurationException)
        {
            result = new AiTestResult(false, AiOutcome.BadRequest, Latency(started), null, null);
        }
        // Gateway answers whatever the request names, so only its model list tells which model really served the test.
        if (result.Ok && profile.Provider == AiProvider.Gateway && (await factory.ProbeAsync(profile, ct)).ServedModel is { } served)
        {
            result = result with { Warning = AiProfileStatus.ModelMismatch(profile.Model, served) };
        }
        return result;
    }

    private async Task<AiTestResult> EmbeddingsAsync(AiProfileSnapshot profile, long started, CancellationToken ct)
    {
        using var generator = factory.Embeddings(profile, AiTaskCatalog.AdminTestEmbedding);
        var embeddings = await generator.GenerateAsync([profile.Prepare(ProbeSentence, AiEmbeddingPurpose.Query)], cancellationToken: ct);
        var embedding = embeddings[0];
        return new AiTestResult(true, null, Latency(started), embedding.Vector.Length, Model(embedding.ModelId, profile));
    }

    /// <summary>Two synthetic documents: the one about the probe sentence must score higher.</summary>
    private async Task<AiTestResult> RerankAsync(AiProfileSnapshot profile, long started, CancellationToken ct)
    {
        var scores = await factory.Rerank(profile, AiTaskCatalog.AdminTestRerank)
            .ScoreAsync(ProbeSentence, ["Připojení k síti VPN z domova přes firemního klienta.", "Jídelní lístek závodní jídelny na pátek."], ct);
        var ok = scores[0] > scores[1];
        return new AiTestResult(ok, ok ? null : AiOutcome.InvalidOutput, Latency(started), null, profile.Model);
    }

    private async Task<AiTestResult> ChatAsync(AiProfileSnapshot profile, long started, CancellationToken ct)
    {
        using var chat = factory.Chat(profile, AiTaskCatalog.AdminTestChat);
        var response = await chat.GetResponseAsync<AiTestAnswer>(
            [
                new ChatMessage(ChatRole.System, "Jsi test spojení wiki Atlas. Odpovídej jen požadovaným JSON objektem."),
                new ChatMessage(ChatRole.User, "Potvrď spojení: vrať objekt s polem ok nastaveným na true."),
            ],
            JsonSerializerOptions.Web, new ChatOptions { MaxOutputTokens = 256 }, cancellationToken: ct);
        var latency = Latency(started);
        return response.TryGetResult(out var answer) && answer.Ok
            ? new AiTestResult(true, null, latency, null, Model(response.ModelId, profile))
            : new AiTestResult(false, AiOutcome.InvalidOutput, latency, null, Model(response.ModelId, profile));
    }

    private int Latency(long started) => (int)Math.Clamp(Math.Round(clock.GetElapsedTime(started).TotalMilliseconds), 0, int.MaxValue);

    private static string Model(string? answered, AiProfileSnapshot profile)
    {
        var model = string.IsNullOrWhiteSpace(answered) ? profile.Model : answered;
        return model.Length > MaxModelLength ? model[..MaxModelLength] : model;
    }

    /// <summary>The test's schema: <c>{ "ok": true }</c>.</summary>
    public sealed record AiTestAnswer(bool Ok);
}
