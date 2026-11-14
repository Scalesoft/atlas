namespace Atlas.Server.Features.PageSource.Contracts;

/// <summary>The page body applied from the source editor. The title is kept; as in a normal save the server writes the Markdown projection
/// from the JSON (<see cref="Rules.ValidateDocument(string?)"/>), so <paramref name="Markdown"/> is accepted for compatibility and ignored.</summary>
public sealed record ReplacePageSourceRequest(string ContentJson, string? Markdown, int ExpectedVersion);
