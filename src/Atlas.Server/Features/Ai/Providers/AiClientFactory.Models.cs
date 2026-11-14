using System.Net.Http.Headers;
using System.Text.Json;
using Atlas.Server.Features.Ai.Settings.Domain;

namespace Atlas.Server.Features.Ai.Providers;

public sealed partial class AiClientFactory
{
    private const int CatalogueLimit = 500;
    private const int CataloguePageLimit = 5;
    private const int CatalogueByteLimit = 1_048_576;

    /// <summary>Metadata only: no chat, embeddings, model load or unload, retries, usage or availability changes.</summary>
    public async Task<AiModelCatalogue> ListModelsAsync(AiProfileSnapshot connection, CancellationToken ct)
    {
        if (!AiProvider.SupportsProfile(connection.Provider, connection.Location, connection.Kind) || connection.BaseUrl is null)
        {
            return new(false, AiOutcome.BadRequest, []);
        }
        if (!TryReadKey(connection, out var key))
        {
            return new(false, AiOutcome.KeyUnreadable, []);
        }
        if ((connection.IsCloud || connection.Provider == AiProvider.Anthropic) && key is null)
        {
            return new(false, AiOutcome.Auth, []);
        }
        using var timeout = new CancellationTokenSource(ProbeTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return new(true, null, await ReadCatalogueAsync(connection, key, linked.Token));
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            var code = timeout.IsCancellationRequested && exception is OperationCanceledException
                ? AiOutcome.Timeout : AiErrorClassifier.Classify(exception, ct);
            logger.LogWarning("AI model catalogue at {Host} failed: {Outcome}", connection.Host, code);
            return new(false, code, []);
        }
    }

    private async Task<IReadOnlyList<AiModelOption>> ReadCatalogueAsync(AiProfileSnapshot connection, string? key, CancellationToken ct)
    {
        var anthropic = connection.Provider == AiProvider.Anthropic;
        var endpoint = connection.BaseUrl!.TrimEnd('/') + (anthropic ? "/v1/models?limit=100" : "/models");
        var models = new Dictionary<string, AiModelOption>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        using var client = http.CreateClient(AiHttp.ClientName);
        for (var page = 0; page < CataloguePageLimit; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + (cursor is null ? "" : "&after_id=" + Uri.EscapeDataString(cursor)));
            if (anthropic)
            {
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", "2023-06-01");
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key ?? KeylessPlaceholder);
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            // Bound the body as well as the returned list; an internal compatible server is still an external boundary.
            await response.Content.LoadIntoBufferAsync(CatalogueByteLimit, ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            AddCataloguePage(json.RootElement, models);
            if (!anthropic || models.Count >= CatalogueLimit || !json.RootElement.TryGetProperty("has_more", out var more) || more.ValueKind != JsonValueKind.True)
            {
                break;
            }
            cursor = ModelText(json.RootElement, "last_id");
            if (cursor is null || !cursors.Add(cursor))
            {
                throw new JsonException("Invalid model catalogue cursor.");
            }
        }
        return models.Values.OrderBy(model => model.Id, StringComparer.Ordinal).ToArray();
    }

    private static void AddCataloguePage(JsonElement root, Dictionary<string, AiModelOption> models)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Invalid model catalogue.");
        }
        foreach (var item in data.EnumerateArray())
        {
            var id = ModelText(item, "id");
            if (id is not null)
            {
                models.TryAdd(id, new(id, ModelText(item, "display_name") ?? id));
            }
            if (models.Count >= CatalogueLimit)
            {
                break;
            }
        }
    }

    private static string? ModelText(JsonElement item, string property)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(property, out var field) || field.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var text = field.GetString();
        return string.IsNullOrWhiteSpace(text) || text.Length > 200 || text.Any(char.IsControl) ? null : text;
    }
}
