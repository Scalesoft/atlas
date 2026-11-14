using Atlas.Server.Features.AiSuggestions.Queries;

namespace Atlas.Server.Features.AiSuggestions;

/// <summary>Where the actor may create in a space (CreatePage's rule): the root with Writer authority on the space (or as
/// WikiAdmin), below a page with EditContent on it. A space the actor cannot create in, or that does not exist, is
/// <see cref="CreationScope.None"/> without telling which.</summary>
public sealed class CreationScopeReader(IContentAccess access, SpaceRoles roles, SemanticSuggestionSql sql)
{
    public async Task<(UserAccount Actor, CreationScope Scope)> ReadAsync(Guid spaceId, bool readInclusion, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(spaceId, ct);
        var root = (await roles.AuthorityAsync(actor, spaceId, SpaceRole.Writer, ct)).Allowed;
        var mayCreate = root || await sql.MayCreateBelowPageAsync(spaceId, actor, ct);
        if (!mayCreate)
        {
            return (actor, CreationScope.None);
        }
        return (actor, new CreationScope(true, root, readInclusion && await sql.SpaceInAiAsync(spaceId, ct)));
    }
}
