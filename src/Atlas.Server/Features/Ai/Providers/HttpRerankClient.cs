using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlas.Server.Features.Ai.Providers;

/// <summary>A cross-encoder of a rerank profile: one relevance score per document, in the documents' order.</summary>
public interface IRerankClient
{
    Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> documents, CancellationToken ct);
}

/// <summary>
/// <c>POST {base}/rerank</c> as llama.cpp (<c>--reranking</c>), Infinity, vLLM and Jina serve it: <c>{model, query, documents, top_n}</c> →
/// <c>{results: [{index, relevance_score}]}</c>. The key, when the profile has one, as a bearer token; on the no-redirect
/// <see cref="AiHttp.ClientName"/> client. A missing or out-of-range index is an unusable answer.
/// </summary>
internal sealed class HttpRerankClient(HttpClient http, AiProfileSnapshot profile, string? key) : IRerankClient
{
    public async Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> documents, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, profile.BaseUrl!.TrimEnd('/') + "/rerank")
        {
            Content = JsonContent.Create(new { model = profile.Model, query, documents, top_n = documents.Count }),
        };
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadFromJsonAsync<RerankAnswer>(ct) ?? throw new JsonException("No rerank answer.");
        var scores = Enumerable.Repeat(double.NaN, documents.Count).ToArray();
        foreach (var result in answer.Results ?? throw new JsonException("No rerank results."))
        {
            if (result.Index < 0 || result.Index >= scores.Length)
            {
                throw new JsonException("A rerank result names no document.");
            }
            scores[result.Index] = result.RelevanceScore;
        }
        return scores;
    }

    private sealed record RerankAnswer(IReadOnlyList<RerankResult>? Results);

    private sealed record RerankResult(int Index, [property: JsonPropertyName("relevance_score")] double RelevanceScore);
}
