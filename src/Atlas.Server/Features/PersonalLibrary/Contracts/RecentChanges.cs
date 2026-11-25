using Atlas.Server.Features.PageHistory.Contracts;

namespace Atlas.Server.Features.PersonalLibrary.Contracts;

/// <summary>
/// One page of Co se změnilo (home dashboard): the page's newest change the caller may see within the window, as a version of the page
/// history (3.6-C), and how many further versions of it the window holds.
/// </summary>
/// <param name="Channel">web, api, mcp, assistant, import or system; null on rows from before 2.0.</param>
/// <param name="IsAi">A change with AI (RevisionVersions.IsAi): over MCP, the personal API, the assistant, or marked as written with AI.</param>
/// <param name="ApiToken">The token of an api or mcp change, for its owner and wiki administrators only (ApiTokenVisibility).</param>
/// <param name="CanOpenHistory">The caller may edit the page, so its history (and this change in it) opens for them.</param>
public sealed record RecentChange(Guid PageId, string Title, Guid? SpaceId, string? SpaceName, Guid RevisionId, int Version, DateTime At,
    Guid AuthorId, string AuthorName, string? Channel, bool IsAi, ApiTokenRef? ApiToken, int FurtherChanges, bool CanOpenHistory);

/// <param name="Next">The cursor of the next page, or null at the end of the window.</param>
public sealed record RecentChangeList(IReadOnlyList<RecentChange> Items, string? Next);

/// <summary>How many versions a person made on <paramref name="Day"/> (yyyy-MM-dd, Prague) in one space; only days with any.</summary>
public sealed record PersonActivityDay(string Day, Guid? SpaceId, string? SpaceName, int Changes);

/// <summary>A person's last 365 days, as GetPersonActivity reads them.</summary>
public sealed record PersonActivity(IReadOnlyList<PersonActivityDay> Days);
