using System.Diagnostics.CodeAnalysis;

namespace Atlas.Server.Features.Ai.Settings.Domain;

/// <summary>
/// One immutable revision of a profile kind (spec RAG §7): a save inserts a new row and moves the
/// <see cref="AiProfileAssignment"/>; the database refuses UPDATE and DELETE. <see cref="ProtectedApiKey"/> is the
/// data-protected key ("" = none) and never leaves the server.
/// </summary>
public class AiProfileRevision
{
    public virtual Guid Id { get; set; }
    /// <summary><see cref="AiProfileKind"/>.</summary>
    public virtual string Kind { get; set; } = "";
    public virtual bool Enabled { get; set; }
    /// <summary><see cref="AiProvider"/>.</summary>
    public virtual string Provider { get; set; } = "";
    /// <summary><see cref="AiLocation"/>.</summary>
    public virtual string Location { get; set; } = "";
    public virtual string? BaseUrl { get; set; }
    public virtual string Model { get; set; } = "";
    public virtual string? ModelVersion { get; set; }
    public virtual string ProtectedApiKey { get; set; } = "";
    public virtual int TimeoutSeconds { get; set; }
    public virtual int MaxConcurrency { get; set; }
    public virtual string? ReasoningEffort { get; set; }
    public virtual float? Temperature { get; set; }
    public virtual bool NativeJsonSchema { get; set; } = true;
    /// <summary>Embeddings only; phase 2 supports 1 024.</summary>
    public virtual int? Dimensions { get; set; }
    public virtual string QueryPrefix { get; set; } = "";
    public virtual string DocumentPrefix { get; set; } = "";
    /// <summary>Embeddings: send <c>dimensions</c> to an OpenAI model that natively returns more.</summary>
    public virtual bool RequestDimensions { get; set; }
    /// <summary>Quick tasks: use the chat profile instead of an own model (from phase 3).</summary>
    public virtual bool UseChatProfile { get; set; }
    /// <summary>Price per million tokens, for the overview's cost estimate only.</summary>
    public virtual decimal? InputPricePerMillion { get; set; }
    public virtual decimal? OutputPricePerMillion { get; set; }
    /// <summary>Embeddings only: <see cref="AiProfileRules.Fingerprint"/>.</summary>
    public virtual string? Fingerprint { get; set; }
    public virtual Guid CreatedById { get; set; }
    public virtual DateTime CreatedAt { get; set; }
}

public static class AiProfileKind
{
    public const string Chat = "chat";
    public const string Embeddings = "embeddings";
    public const string Quick = "quick";
    /// <summary>A cross-encoder that reorders search and answer candidates (<c>search.rerank</c>, migration 2026102040).</summary>
    public const string Rerank = "rerank";

    public static readonly IReadOnlyList<string> All = [Chat, Embeddings, Quick, Rerank];

    public static bool IsKnown(string? kind) => kind is Chat or Embeddings or Quick or Rerank;
}

public static class AiProvider
{
    public const string OpenAiCompatible = "openai-compatible";
    public const string Anthropic = "anthropic";
    public const string Gateway = "gateway";

    public static bool UsesOpenAiProtocol([NotNullWhen(true)] string? provider)
        => provider is OpenAiCompatible or Gateway;

    public static bool IsKnown([NotNullWhen(true)] string? provider)
        => UsesOpenAiProtocol(provider) || provider == Anthropic;

    public static bool SupportsProfile(string provider, string location, string kind)
        => IsKnown(provider) && (provider != Gateway || (location == AiLocation.Company && kind is AiProfileKind.Chat or AiProfileKind.Quick));
}

public static class AiLocation
{
    public const string Company = "company";
    public const string Cloud = "cloud";
}
