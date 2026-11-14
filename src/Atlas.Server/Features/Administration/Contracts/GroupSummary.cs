namespace Atlas.Server.Features.Administration.Contracts;

public sealed record GroupSummary(Guid Id, string Name, string Version, string Source = "local", string? HubUrl = null);
