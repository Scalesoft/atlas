namespace Atlas.Server.Features.Administration.Queries;

/// <summary>One page (<see cref="AdministrationReads.PageLimit"/> rows) from <paramref name="Offset"/>, optionally only the accounts
/// matching <paramref name="Search"/> or only <paramref name="Id"/>.</summary>
public sealed record ListUsers(string? Search = null, int Offset = 0, Guid? Id = null);
