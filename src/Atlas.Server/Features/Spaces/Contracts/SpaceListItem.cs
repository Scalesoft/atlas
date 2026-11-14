using Atlas.Server.Features.SpaceCovers.Contracts;

namespace Atlas.Server.Features.Spaces.Contracts;

/// <param name="Role">The space role; "Reader" for an account that reaches only single pages of the space (ItemAccessOnly).</param>
public sealed record SpaceListItem(Guid Id, string Name, string Icon, string Color, string Role, string Description)
{
    /// <summary>D8: the account reaches only single pages of the space. It sees the name, icon and colour, not the description.</summary>
    public bool ItemAccessOnly { get; init; }

    /// <summary>None, Default or Required (SpacePinPolicy).</summary>
    public string PinPolicy { get; init; } = nameof(SpacePinPolicy.None);

    /// <summary>Effective for this account: Required, else its own choice, else Default.</summary>
    public bool Pinned { get; init; }

    /// <summary>The pages the account sees in the tree of the space.</summary>
    public int PageCount { get; init; }

    /// <summary>The newest revision the account may read among those pages: the working one where it edits the page, the published
    /// one otherwise. Null without pages.</summary>
    public DateTime? LastChangedAt { get; init; }

    /// <summary>The image of the space, shown to whoever lists it, ItemAccessOnly included; never null. An automatic pick is
    /// computed from what this account sees of the space.</summary>
    public SpaceCoverDto Cover { get; init; } = SpaceCoverDto.Unmatched;
}
