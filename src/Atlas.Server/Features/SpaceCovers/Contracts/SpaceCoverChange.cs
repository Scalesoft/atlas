namespace Atlas.Server.Features.SpaceCovers.Contracts;

/// <summary>The image after a change, and the file the change released. The endpoint deletes that file only after the commit:
/// a rolled-back change must still find it.</summary>
public sealed record SpaceCoverChange(SpaceCoverDto Cover, Guid? ReleasedFileId);
