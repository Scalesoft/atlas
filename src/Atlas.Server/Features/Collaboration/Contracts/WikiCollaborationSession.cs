namespace Atlas.Server.Features.Collaboration.Contracts;

/// <param name="Upgraded">The open moved a live state of a previous host schema to this generation (LiveRequirementsUpgrade); not sent.</param>
public sealed record WikiCollaborationSession(string DocumentId, int CurrentVersion, Guid Generation,
    [property: System.Text.Json.Serialization.JsonIgnore] bool Upgraded = false);
