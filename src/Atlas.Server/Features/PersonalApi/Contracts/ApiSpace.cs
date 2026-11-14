namespace Atlas.Server.Features.PersonalApi.Contracts;

/// <param name="Role">The space role; "Reader" for an account that reaches only single pages of the space (ItemAccessOnly).</param>
public sealed record ApiSpace(Guid Id, string Name, string Icon, string Color, string Role, string Description = "")
{
    /// <summary>The account reaches only single pages of the space: name, icon and colour only, no description.</summary>
    public bool ItemAccessOnly { get; init; }
}
