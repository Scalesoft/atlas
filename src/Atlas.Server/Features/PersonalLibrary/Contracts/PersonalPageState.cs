namespace Atlas.Server.Features.PersonalLibrary.Contracts;

public sealed record PersonalPageState(bool IsFavorite, DateTime? VisitedAt);
