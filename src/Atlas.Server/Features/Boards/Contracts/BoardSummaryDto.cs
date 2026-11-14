namespace Atlas.Server.Features.Boards.Contracts;

/// <summary>A board in the list of its space. ItemCount counts the live items.</summary>
public sealed record BoardSummaryDto(Guid Id, string Title, DateTime UpdatedAt, int ItemCount);
