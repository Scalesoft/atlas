namespace Atlas.Server.Features.Administration.Contracts;

public sealed record GroupMemberSummary(Guid Id, string Username, string DisplayName, bool IsActive, string Version);
