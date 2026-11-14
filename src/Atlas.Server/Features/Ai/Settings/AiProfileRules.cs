using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;

namespace Atlas.Server.Features.Ai.Settings;

/// <summary>
/// Validation of a profile save (400 with Czech texts), the embedding fingerprint and the origin a stored key is bound to.
/// Pure functions: the handler adds the key, the compare-and-swap and the index guard around them.
/// </summary>
public static class AiProfileRules
{
    /// <summary>Phase 2 stores <c>vector(1024)</c> (O3): bge-m3, Qwen3-Embedding 0.6B, OpenAI with <c>dimensions=1024</c>.</summary>
    public const int SupportedDimensions = 1024;
    private const string AnthropicDefaultOrigin = "https://api.anthropic.com";
    public const string OpenAiDefaultBaseUrl = "https://api.openai.com/v1";

    /// <summary>The kind of a route or message; anything else is an unknown resource.</summary>
    public static string RequireKind(string? kind)
        => AiProfileKind.IsKnown(kind) ? kind! : throw new ApiException(404, "Takový profil AI neexistuje.");

    /// <summary>Timeout and concurrency a kind starts with before its first save.</summary>
    public static (int TimeoutSeconds, int MaxConcurrency) Defaults(string kind) => kind switch
    {
        AiProfileKind.Chat => (120, 2),
        AiProfileKind.Quick => (30, 2),
        AiProfileKind.Rerank => (10, 4),
        _ => (60, 4),
    };

    /// <summary>
    /// A new, not yet saved revision from the request: validated, trimmed, and with the fields that do not apply to the
    /// kind reset. Neither the key (<see cref="NewApiKey"/>) nor the usability of an enabled profile
    /// (<see cref="RequireUsable"/>) is decided here, because both depend on the stored revision.
    /// </summary>
    public static AiProfileRevision Draft(string kind, SaveAiProfileRequest request)
    {
        var (provider, location) = ValidateProvider(kind, request.Provider, request.Location);
        var embeddings = kind == AiProfileKind.Embeddings;
        if (embeddings && request.Dimensions != SupportedDimensions)
        {
            throw new ApiException(400, "Atlas 2.0 podporuje embeddingy s 1 024 rozměry.");
        }
        var baseUrl = ConnectionUrl(request.BaseUrl, provider, location);
        var model = Name(request.Model, "Název modelu");
        var modelVersion = Name(request.ModelVersion, "Verze modelu") is { Length: > 0 } version ? version : null;
        ValidateExecution(request);
        var revision = new AiProfileRevision
        {
            Kind = kind, Enabled = request.Enabled, Provider = provider, Location = location, BaseUrl = baseUrl, Model = model,
            ModelVersion = modelVersion, TimeoutSeconds = request.TimeoutSeconds, MaxConcurrency = request.MaxConcurrency,
            InputPricePerMillion = request.InputPricePerMillion,
        };
        if (embeddings)
        {
            ConfigureEmbeddings(revision, request);
            return revision;
        }
        if (kind == AiProfileKind.Rerank)
        {
            // Scores only: no reasoning, temperature, JSON schema or output price.
            return revision;
        }
        ValidateChatOptions(request);
        revision.ReasoningEffort = request.ReasoningEffort;
        revision.Temperature = request.Temperature;
        revision.NativeJsonSchema = request.NativeJsonSchema;
        revision.OutputPricePerMillion = request.OutputPricePerMillion;
        revision.UseChatProfile = kind == AiProfileKind.Quick && request.UseChatProfile;
        return revision;
    }

    /// <summary>The new key of the request, or null to keep (or clear) the stored one.</summary>
    public static string? NewApiKey(SaveAiProfileRequest request) => NewApiKey(request.ApiKey, request.ClearApiKey);

    private static string? NewApiKey(string? apiKey, bool clearApiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }
        if (clearApiKey)
        {
            throw new ApiException(400, "Klíč API nelze současně změnit a odstranit.");
        }
        var key = apiKey.Trim();
        if (key.Length > 8192 || key.Any(char.IsControl))
        {
            throw new ApiException(400, "Klíč API nemá platný formát.");
        }
        return key;
    }

    /// <summary>An enabled profile must be callable: a model, a URL for OpenAI-compatible servers and a key for the cloud and
    /// Anthropic. Company servers may run without a key. Quick tasks that use the chat profile need nothing of their own.</summary>
    public static void RequireUsable(AiProfileRevision revision)
    {
        if (!revision.Enabled || revision.UseChatProfile)
        {
            return;
        }
        if (revision.Model.Length == 0)
        {
            throw new ApiException(400, "Pro zapnutí profilu vyplňte model.");
        }
        if (AiProvider.UsesOpenAiProtocol(revision.Provider) && revision.BaseUrl is null)
        {
            throw new ApiException(400, "Pro zapnutí profilu vyplňte adresu API.");
        }
        if ((revision.Location == AiLocation.Cloud || revision.Provider == AiProvider.Anthropic) && revision.ProtectedApiKey.Length == 0)
        {
            throw new ApiException(400, "Pro zapnutí cloudového profilu nebo Anthropic zadejte klíč API.");
        }
    }

    /// <summary>
    /// What decides whether an index generation stays compatible (R3): provider, model, model version, dimensions and
    /// prefixes. Not the URL, key, timeout or concurrency, so a key rotation or a TLS proxy in front of LM Studio keeps the
    /// index. Lowercase hex SHA-256 of a JSON array, so no two field combinations share an input.
    /// </summary>
    public static string Fingerprint(string provider, string model, string? modelVersion, int dimensions, string queryPrefix, string documentPrefix)
    {
        var canonical = JsonSerializer.Serialize(new object?[] { "atlas-embeddings-v1", provider, model, modelVersion ?? "", dimensions, queryPrefix, documentPrefix });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>The provider and origin (scheme, host, port) a stored key belongs to. A key never follows the profile to
    /// another one; a different path on the same server keeps it.</summary>
    public static string KeyOrigin(AiProfileRevision revision)
    {
        var url = ConnectionUrl(revision.BaseUrl, revision.Provider, revision.Location);
        return revision.Provider + " " + (url is null ? "" : new Uri(url).GetLeftPart(UriPartial.Authority).ToLowerInvariant());
    }

    private static (string Provider, string Location) ValidateProvider(string kind, string? requestedProvider, string? requestedLocation)
    {
        var provider = requestedProvider?.Trim();
        if (!AiProvider.IsKnown(provider))
        {
            throw new ApiException(400, "Vyberte poskytovatele OpenAI-kompatibilní API, Anthropic nebo Gateway.");
        }
        var location = requestedLocation?.Trim();
        if (location is not (AiLocation.Company or AiLocation.Cloud))
        {
            throw new ApiException(400, "Umístění modelu musí být firemní server, nebo cloud.");
        }
        if (!AiProvider.SupportsProfile(provider, location, kind))
        {
            throw new ApiException(400, "Gateway je dostupná jen na firemním serveru pro Chat a Rychlé úlohy.");
        }
        if (kind == AiProfileKind.Embeddings && provider == AiProvider.Anthropic)
        {
            throw new ApiException(400, "Anthropic neposkytuje embeddingy; použijte LM Studio nebo OpenAI.");
        }
        if (kind == AiProfileKind.Rerank && provider != AiProvider.OpenAiCompatible)
        {
            throw new ApiException(400, "Reranker běží na OpenAI-kompatibilním serveru s /rerank (llama.cpp, TEI, Infinity).");
        }
        return (provider, location);
    }

    private static void ValidateExecution(SaveAiProfileRequest request)
    {
        if (request.TimeoutSeconds is < 1 or > 300)
        {
            throw new ApiException(400, "Časový limit musí být 1 až 300 sekund.");
        }
        if (request.MaxConcurrency is < 1 or > 16)
        {
            throw new ApiException(400, "Souběh musí být 1 až 16 požadavků.");
        }
        Price(request.InputPricePerMillion);
        Price(request.OutputPricePerMillion);
    }

    private static void ValidateChatOptions(SaveAiProfileRequest request)
    {
        if (request.ReasoningEffort is not (null or "none" or "low" or "medium" or "high"))
        {
            throw new ApiException(400, "Úroveň uvažování musí být none, low, medium nebo high.");
        }
        if (request.Temperature is { } temperature && !(temperature is >= 0 and <= 2))
        {
            throw new ApiException(400, "Teplota musí být mezi 0 a 2.");
        }
    }

    private static void ConfigureEmbeddings(AiProfileRevision revision, SaveAiProfileRequest request)
    {
        // Prefixes are kept verbatim: trailing spaces and the newline of instruction prefixes (Qwen3) matter.
        revision.Dimensions = SupportedDimensions;
        revision.QueryPrefix = Prefix(request.QueryPrefix);
        revision.DocumentPrefix = Prefix(request.DocumentPrefix);
        revision.RequestDimensions = request.RequestDimensions;
        revision.Fingerprint = Fingerprint(revision.Provider, revision.Model, revision.ModelVersion, SupportedDimensions, revision.QueryPrefix, revision.DocumentPrefix);
    }

    public static AiProfileRevision ConnectionDraft(string kind, DiscoverAiModelsRequest request)
    {
        var (provider, location) = ValidateProvider(RequireKind(kind), request.Provider, request.Location);
        return new AiProfileRevision
        {
            Kind = kind, Provider = provider, Location = location,
            BaseUrl = ConnectionUrl(request.BaseUrl, provider, location), TimeoutSeconds = 5, MaxConcurrency = 1,
        };
    }

    public static void ApplyKey(AiProfileRevision revision, AiProfileRevision? previous, string? apiKey, bool clearApiKey, IDataProtector protector)
    {
        var key = NewApiKey(apiKey, clearApiKey);
        revision.ProtectedApiKey = key is not null ? protector.Protect(key)
            : !clearApiKey && previous is not null && KeyOrigin(previous) == KeyOrigin(revision) ? previous.ProtectedApiKey : "";
    }

    private static string? ConnectionUrl(string? value, string provider, string location)
        => BaseUrl(value, location) ?? (provider == AiProvider.Anthropic ? AnthropicDefaultOrigin
            : provider == AiProvider.OpenAiCompatible && location == AiLocation.Cloud ? OpenAiDefaultBaseUrl : null);

    private static string? BaseUrl(string? value, string location)
    {
        var url = value?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }
        if (url.Length > 2048 || url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || url.IndexOfAny(['?', '#']) >= 0
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0 || uri.Host.Length == 0)
        {
            throw new ApiException(400, "Adresa API musí být úplná adresa http nebo https bez přihlašovacích údajů, parametrů a fragmentu.");
        }
        if (location == AiLocation.Cloud && uri.Scheme != "https")
        {
            throw new ApiException(400, "Cloudový poskytovatel vyžaduje adresu HTTPS.");
        }
        return url;
    }

    private static string Name(string? value, string label)
    {
        var name = value?.Trim() ?? "";
        if (name.Length > 200 || name.Any(char.IsControl))
        {
            throw new ApiException(400, "{label} může mít nejvýše 200 znaků bez řídicích znaků.", args: new Dictionary<string, object?> { ["label"] = label });
        }
        return name;
    }

    private static string Prefix(string? value)
    {
        var prefix = value ?? "";
        if (prefix.Length > 200 || prefix.Any(c => char.IsControl(c) && c != '\n'))
        {
            throw new ApiException(400, "Prefix embeddingů může mít nejvýše 200 znaků; z řídicích znaků jen konec řádku.");
        }
        return prefix;
    }

    private static void Price(decimal? price)
    {
        if (price is < 0 or >= 100_000_000)
        {
            throw new ApiException(400, "Cena za milion tokenů musí být nezáporná a menší než 100 000 000.");
        }
    }
}
