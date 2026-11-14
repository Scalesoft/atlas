using Atlas.Server.Features.StorageProfiles;
namespace Atlas.Server.Features.PagePdfExports;

public class PagePdfExport
{
    public virtual Guid PageId { get; set; }
    public virtual Guid StorageProfileId { get; set; }
    public virtual Guid OwnerId { get; set; }
    public virtual bool Enabled { get; set; } = true;
    public virtual string Folder { get; set; } = "";
    public virtual string FileName { get; set; } = "document.pdf";
    public virtual bool KeepVersions { get; set; }
    public virtual bool PublishedOnly { get; set; }
    /// <summary>What the PDF cover states about the revision (<see cref="ContentExport.PdfCoverDetails"/>), comma-separated.</summary>
    public virtual string CoverDetails { get; set; } = ContentExport.PdfCoverDetails.Default;
    public virtual string SourceOrigin { get; set; } = "";
    public virtual int Version { get; set; } = 1;
    public virtual Guid? LastRevisionId { get; set; }
    public virtual int LastConfigurationVersion { get; set; }
    public virtual int LastProfileVersion { get; set; }
    public virtual DateTime? LastExportedAt { get; set; }
    /// <summary>Where the export last wrote its PDF: removed when the target changes, reserved from other pages until then.</summary>
    public virtual Guid? LastStorageProfileId { get; set; }
    public virtual string? LastFolder { get; set; }
    public virtual string? LastFileName { get; set; }
    public virtual string LastError { get; set; } = "";
    public virtual int FailureCount { get; set; }
    public virtual DateTime NextAttemptAt { get; set; }
    public virtual Guid? RunNowRevisionId { get; set; }
}
/// <param name="CoverDetails">What the PDF cover states about the revision; null keeps the saved choice (all for a new export).</param>
/// <param name="ConfirmTakeover">The person confirmed that the export takes over and replaces a file it did not write.</param>
public sealed record PagePdfExportRequest(bool Enabled, Guid? StorageProfileId, string Folder, string FileName, bool KeepVersions, bool PublishedOnly, int ExpectedVersion,
    IReadOnlyList<string>? CoverDetails = null, bool ConfirmTakeover = false);
public sealed record PagePdfExportSettings(bool Enabled, Guid? StorageProfileId, string Folder, string FileName, bool KeepVersions, bool PublishedOnly, int Version, IReadOnlyList<string> CoverDetails);
public sealed record PagePdfExportView(PagePdfExportSettings Settings, IReadOnlyList<AvailableStorageProfile> Profiles, string Status, DateTime? LastExportedAt, Guid? LastRevisionId, string LastError, string? LastFileName = null);
public sealed record GetPagePdfExport(Guid PageId);
public sealed record SavePagePdfExport(Guid PageId, PagePdfExportRequest Request, string SourceOrigin);
public sealed record RunPagePdfExport(Guid PageId);
public sealed record PagePdfExportFolderRequest(Guid StorageProfileId, string Parent, string Name);
public sealed record PagePdfExportFolders(string Folder, IReadOnlyList<string> Folders, bool Truncated);
public sealed record PagePdfExportTarget(Guid StorageProfileId, string Folder, string FileName);
public sealed record PagePdfExportOverviewRow(Guid PageId, Guid SpaceId, string SpaceName, string PageTitle, string ProfileName, string Path,
    bool Enabled, bool PublishedOnly, bool KeepVersions, string Status, DateTime? LastExportedAt, string LastError);
