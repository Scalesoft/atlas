namespace Atlas.Server.Features.Objects.Commands;

public sealed record TrashObject(Guid ObjectId);

/// <summary>Back from the trash, as it was; a new version.</summary>
public sealed record RestoreObject(Guid ObjectId);

/// <summary>Permanently, only from the trash; an audit row stays.</summary>
public sealed record PurgeObject(Guid ObjectId);
