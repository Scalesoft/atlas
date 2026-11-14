namespace Atlas.Server.Features.Pages.Contracts;

/// <summary>The server writes the stored Markdown from <paramref name="ContentJson"/> (<see cref="Rules.ValidateDocument(string?)"/>):
/// <paramref name="Markdown"/> is accepted for compatibility (the web and older API clients send it) and ignored.
/// <paramref name="ContentJson"/> may be left out (null or empty) only with <paramref name="CollaborationGeneration"/>: the live document as the server projects it.
/// <paramref name="AiAssisted"/> is what the client says (the editor applied an AI proposal); the server stores it on the revision for
/// history and reports but cannot verify it, so it is never a security property or a basis for a decision.</summary>
public sealed record SavePageRequest(string Title, string ContentJson, string? Markdown, int ExpectedVersion, Guid? CollaborationGeneration = null, bool AiAssisted = false);
