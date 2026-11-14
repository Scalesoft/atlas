namespace Atlas.Server.Platform.Content;

/// <summary>
/// A heading and everything up to the next heading of the same or a higher level, subsections included (D8). Lines are
/// 1-based and inclusive in <see cref="ContentProjection.Markdown"/>. A heading without a valid, unique id gets the
/// temporary <c>v{version}-h{n}</c> (n-th heading), valid only together with the same expected version.
/// </summary>
public sealed record ContentSection(string HeadingId, bool HeadingIdIsTemporary, int Level, string Title, int StartLine, int EndLine);
