namespace Atlas.Server.Features.Objects.Queries;

/// <param name="Names">Exact titles (case aside), any of them: a batch lookup by name under the same access rules as every listing.</param>
/// <param name="Page">From 1; <see cref="PageSize"/> objects each.</param>
public sealed record ListObjects(string? Type, string? Scope, Guid? SpaceId, string? Q, int Page = 1, IReadOnlyList<string>? Names = null)
{
    public const int PageSize = 50;
}
