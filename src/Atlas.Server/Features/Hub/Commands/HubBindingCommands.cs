using Atlas.Server.Features.Hub.Contracts;
using Atlas.Server.Features.Hub.Directory;

namespace Atlas.Server.Features.Hub.Commands;

public sealed record CommitHubGroup(Guid ConfigurationId, string? ExpectedVersion, HubDirectoryGroup Group);
public sealed record ChangeHubBinding(Guid GroupId, UpdateHubBinding Request);

