namespace Atlas.Server.Features.PersonalApi.Contracts;

public sealed record ApiList<T>(IReadOnlyList<T> Items, int Offset, int Limit, bool HasMore);
