namespace Atlas.Server.Platform.Storage;

/// <summary>
/// Metadata of a stored file for change detection. <see cref="FileId"/> and <see cref="ChangeTimeUtc"/> are
/// reported only where the storage exposes them (SMB); a local file reports length and last write time.
/// </summary>
public sealed record FileStorageMetadata(long Length, DateTime LastWriteUtc, DateTime? ChangeTimeUtc, string? FileId);
