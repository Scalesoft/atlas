namespace Atlas.Server.Features.PageHistory.Queries;

/// <param name="Group">A version's <c>GroupId</c>: list its steps (checkpoints) instead of the versions.</param>
public sealed record GetPageHistory(Guid Id, int? BeforeVersion, int Limit, Guid? Group = null);
