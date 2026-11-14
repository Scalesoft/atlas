namespace Atlas.Server.Features.SearchGaps.Queries;

public sealed record GetSearchGaps(Guid SpaceId, int Days)
{
    public static readonly int[] AllowedDays = [7, 30, 90];
}
