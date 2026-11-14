namespace Atlas.Server.Features.ContentImport.Domain;

public sealed record WikiSourceFile(string Path, byte[] Bytes, string Sha256);
