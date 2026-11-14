using System.Text.Json;
using Atlas.Server.Features.ModuleSettings.Contracts;

namespace Atlas.Server.Features.ModuleSettings.Domain;

/// <summary>Fetches a provider's discovery document and reports whether it matches the configured issuer.
/// Bounded to 256 KiB and to the named client's timeout; never follows redirects.</summary>
internal static class OidcDiscoveryProbe
{
    private const int MaxDocumentBytes = 256 * 1024;
    private const int ChunkBytes = 8192;

    public static async Task<OidcConnectionTest> ProbeAsync(HttpClient client, string issuer, CancellationToken ct)
    {
        // ResponseHeadersRead stops HttpClient's timeout at headers; keep the same budget for the body.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(client.Timeout);
        try
        {
            using var response = await client.GetAsync(issuer.TrimEnd('/') + "/.well-known/openid-configuration", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxDocumentBytes)
            {
                return new OidcConnectionTest(false, DateTime.UtcNow, "Discovery dokument není dostupný nebo má neplatnou velikost.");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[ChunkBytes];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + count > MaxDocumentBytes)
                {
                    return new OidcConnectionTest(false, DateTime.UtcNow, "Discovery dokument je příliš velký.");
                }
                await buffer.WriteAsync(chunk.AsMemory(0, count), timeout.Token);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            var root = json.RootElement;
            var valid = root.TryGetProperty("issuer", out var found) && found.GetString() == issuer
                && IsHttpsEndpoint(root, "authorization_endpoint") && IsHttpsEndpoint(root, "token_endpoint");
            return new OidcConnectionTest(valid, DateTime.UtcNow, valid
                ? "Discovery je dostupné a issuer souhlasí. Client ID a secret ověříte skutečným přihlášením po restartu."
                : "Discovery neodpovídá zadanému issueru nebo neobsahuje platné HTTPS endpointy.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new OidcConnectionTest(false, DateTime.UtcNow, "Spojení se nepodařilo ověřit. Zkontrolujte issuer, důvěryhodnost certifikátu a dostupnost ze serveru.");
        }
    }

    private static bool IsHttpsEndpoint(JsonElement root, string key)
    {
        return root.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String
            && Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri)
            && uri.Scheme == "https"
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment);
    }
}
