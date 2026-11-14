using System.Reflection;
using System.Text.RegularExpressions;
using Atlas.Server.Features.Pages.Domain;
using Xunit;

namespace Atlas.Server.Tests.AccessAudit;

/// <summary>
/// Since 3.6 (migration 2026110941) no trigger closes external share links: the code that ends their issuer's authority does, through
/// ShareRevocation. Rights changes reach it through AccountHistory and AccessAuditLog.ClosedAsync (AccessAuditCoverageTests); this test
/// covers what moves or trashes a page. Over the server's IL per method: a method that changes Page.SpaceId or Page.IsTrashed, or holds an
/// UPDATE of either in its SQL text, calls ShareRevocation, or is listed below with the reason it need not. The list is exact both ways.
/// </summary>
[Trait("Category", "Guard")]
public sealed partial class ShareRevocationCoverageTests
{
    /// <summary>Movers and trashers of pages that close no link themselves, by site, and why.</summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["Atlas.Server.Features.Backups.Archive.WikiBackupImport.ImportPageAnchorsAsync"] =
            "places the imported pages; ImportContentAsync closes the links of the moved and newly trashed ones",
        ["Atlas.Server.Features.Backups.Archive.WikiBackupImport.RestorePlacement"] =
            "places the imported pages; ImportContentAsync closes the links of the moved and newly trashed ones",
        ["Atlas.Server.Features.ContentImport.Commands.CommitImportHandler.Handle"] = "places the pages it creates: a new page has no link",
        ["Atlas.Server.Features.PageOrganization.Domain.PageTrash.Restore"] = "untrashes: a closed link never reopens",
        ["Atlas.Server.Features.Pages.Commands.CreatePageHandler.Handle"] = "places the page it creates: a new page has no link",
    };

    [Fact]
    public void Every_mover_and_trasher_of_pages_closes_the_share_links_it_ends()
    {
        var problems = CoverageScan.Problems(
            callee => CoverageScan.Sets(callee, typeof(Page), nameof(Page.SpaceId), nameof(Page.IsTrashed)),
            text => SqlWrite().IsMatch(text),
            callee => callee.DeclaringType == typeof(ShareRevocation),
            Exempt, "moves or trashes a page without closing its share links (ShareRevocation)", minimum: 3);
        Assert.True(problems.Length == 0, string.Join("\n", problems));
    }

    [GeneratedRegex("""UPDATE\s+(ONLY\s+)?(\w+\.)?"Page"\s[^;]*\bSET\b[^;]*"(SpaceId|IsTrashed)"\s*=""", RegexOptions.IgnoreCase)]
    private static partial Regex SqlWrite();
}
