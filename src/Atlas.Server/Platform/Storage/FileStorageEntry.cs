namespace Atlas.Server.Platform.Storage;

/// <summary>A file of a storage folder listing: its portable name, length and last write time.</summary>
public sealed record FileStorageEntry(string Name, long Length, DateTime LastWriteUtc);

/// <summary>Files matching an extension, at most the requested number; Truncated tells that more matched.</summary>
public sealed record FileStorageListing(IReadOnlyList<FileStorageEntry> Files, bool Truncated);
