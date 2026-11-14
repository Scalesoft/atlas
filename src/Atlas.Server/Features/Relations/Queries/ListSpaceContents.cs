namespace Atlas.Server.Features.Relations.Queries;

/// <param name="Type">Only objects of this type id.</param>
/// <param name="Relation">unused, noIncoming, broken, attached or mostUsed (spec §3.2); null: the default listing.</param>
/// <param name="Sort">title (default), updated or used (most incoming first; the default of mostUsed).</param>
/// <param name="Cursor">nextCursor of the previous page.</param>
public sealed record ListSpaceContents(Guid SpaceId, string? Type = null, string? Relation = null, string? Q = null, string? Sort = null, string? Cursor = null)
{
    public const int PageSize = 50;
}
