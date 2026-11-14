namespace Atlas.Server.Features.PageOrganization.Contracts;

/// <summary>A node of the space's trash: a page, or another content type's (<see cref="Type"/>; Version 0).</summary>
public sealed record TrashedPageItem(Guid Id, Guid? ParentId, string Title, int Version, DateTime TrashedAt, Guid TrashedById, Guid TrashBatchId)
{
    public string Type { get; init; } = "page";

    /// <summary>Who moved it to the trash, for the trash list's "kdo a kdy".</summary>
    public string? TrashedByName { get; init; }
}
