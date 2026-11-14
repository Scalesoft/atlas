namespace Atlas.Server.Features.ContentExport.Contracts;

/// <summary>
/// A finished private archive plus the selection the caller must revalidate before the bytes leave the server.
/// Deliberately does NOT implement IAsyncDisposable/IDisposable: this record is returned from
/// BuildContentExportHandler through IMessageBus.InvokeAsync&lt;ContentExportArchive&gt;(). The real mechanism:
/// JasperFx.CodeGeneration's Frames/MethodCall.cs wraps a frame's return variable in using/await using when it
/// implements IDisposable/IAsyncDisposable and DisposalMode == DisposalMode.UsingBlock (the default), so the
/// generated handler code disposes a disposable result before InvokeAsync&lt;T&gt; returns it to the caller
/// (observed empirically: ZipArchive/Results.File then fail with "Cannot use read mode on a non-readable
/// stream."). DisposeAsync stays as a plain method so `await using` and explicit cleanup on the failure path
/// still work by duck typing. Same reason AttachmentContentDto (spec §7.2) carries no open FileStream through
/// a message.
/// </summary>
public sealed record ContentExportArchive(Stream Stream, string Name, string Sha256, Guid SpaceId, IReadOnlyList<ExportSelection> Selected)
{
    public ValueTask DisposeAsync()
    {
        return Stream.DisposeAsync();
    }
}
