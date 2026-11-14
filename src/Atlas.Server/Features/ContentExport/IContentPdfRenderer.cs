using Atlas.Server.Features.ContentExport.Contracts;

namespace Atlas.Server.Features.ContentExport;

/// <summary>Creates a PDF from the already authorized immutable page snapshot.</summary>
public interface IContentPdfRenderer
{
    /// <summary>An interactive export: refused with <c>pdf_busy</c> while another one prints, waits briefly for an automatic one.</summary>
    Task<ContentExportArchive> RenderAsync(ContentExportArchive archive, string sourceOrigin, IReadOnlyList<string> coverDetails, CancellationToken ct);

    /// <summary>
    /// The render slot for an automatic export, reserved before its snapshot is built and released by disposing it; null while
    /// another export prints or an interactive one waits for the slot (try again later).
    /// </summary>
    IContentPdfSlot? TryReserveAutomatic();
}

/// <summary>A reserved render slot (<see cref="IContentPdfRenderer.TryReserveAutomatic"/>).</summary>
public interface IContentPdfSlot : IDisposable
{
    /// <param name="coverDetails">What the cover states about the revision (<see cref="PdfCoverDetails"/>).</param>
    Task<ContentExportArchive> RenderAsync(ContentExportArchive archive, string sourceOrigin, IReadOnlyList<string> coverDetails, CancellationToken ct);
}
