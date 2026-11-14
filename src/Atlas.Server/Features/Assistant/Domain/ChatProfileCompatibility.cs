using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Domain;

namespace Atlas.Server.Features.Assistant.Domain;

/// <summary>
/// Whether a conversation may continue on the current chat profile revision (plan phase 3 R5, O4; spec RAG §3: a change must
/// not silently carry a conversation's content to another provider). Compatible = the same provider, location, URL origin
/// (scheme, host, port) and model. A key rotation, a timeout, the concurrency, the reasoning effort or another path on the same
/// server keep it, so the conversation silently moves to the new revision; anything else leaves it read-only ("Tato konverzace
/// používá předchozí nastavení."). The fingerprint is stored with the conversation (<c>AiConversation.ChatCompatibility</c>).
/// </summary>
public static class ChatProfileCompatibility
{
    public static string Fingerprint(AiProfileSnapshot profile) => Fingerprint(profile.Provider, profile.Location, profile.BaseUrl, profile.Model);

    /// <summary>Lowercase hex SHA-256 of a JSON array, so no two field combinations share an input (like the embeddings fingerprint).</summary>
    public static string Fingerprint(string provider, string location, string? baseUrl, string model)
    {
        var canonical = JsonSerializer.Serialize(new object?[] { "atlas-chat-v1", provider, location, Origin(provider, baseUrl), model });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>True when a conversation pinned with <paramref name="pinned"/> may continue on <paramref name="current"/>.</summary>
    public static bool IsCompatible(string pinned, AiProfileSnapshot current)
        => string.Equals(pinned, Fingerprint(current), StringComparison.Ordinal);

    /// <summary>Scheme, host and port in lowercase, the default port left out; Anthropic without a URL is its official API.</summary>
    private static string Origin(string provider, string? baseUrl)
    {
        var url = baseUrl ?? (provider == AiProvider.Anthropic ? AiProfileSnapshot.AnthropicDefaultBaseUrl : null);
        return url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant()
            : "";
    }
}
