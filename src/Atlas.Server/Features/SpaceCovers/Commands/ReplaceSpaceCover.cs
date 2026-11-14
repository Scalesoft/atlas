namespace Atlas.Server.Features.SpaceCovers.Commands;

/// <param name="Content">The uploaded bytes; the endpoint refuses a file above SpaceCoverImage.MaximumBytes before reading it.</param>
public sealed record ReplaceSpaceCover(Guid SpaceId, byte[] Content);
