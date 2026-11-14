namespace Atlas.Server.Features.Collaboration.Contracts;

public sealed record CollaborationCheckpoint(long Sequence, string EditorJson);
