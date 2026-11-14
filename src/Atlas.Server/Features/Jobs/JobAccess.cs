using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Jobs;

/// <summary>Who sees and does what with background jobs (3.3 plan §4.6): a wiki administrator everything, a space administrator the runs of
/// their own spaces (run now, retry, cancel, never pause), readers and guests nothing. A token is its owner within its scope, which the
/// route's policy checks; the same handlers serve the web, /api/v1 and MCP.</summary>
public static class JobAccess
{
    public static async Task<JobViewer> ViewerAsync(IContentAccess access, SpaceRoles roles, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(null, ct);
        if (await roles.IsWikiAdminAsync(actor, ct))
        {
            return new JobViewer(actor.Id, actor.DisplayName, true, []);
        }
        var spaces = (await roles.EffectiveAsync(actor, ct)).Where(space => space.Value == SpaceRole.Admin).Select(space => space.Key).ToList();
        return spaces.Count == 0
            ? throw new ApiException(403, "Úlohy na pozadí vidí správce wiki a správce prostoru.", "forbidden")
            : new JobViewer(actor.Id, actor.DisplayName, false, spaces);
    }
}

/// <param name="Spaces">The spaces the viewer administers; empty for a wiki administrator, who sees every run.</param>
public sealed record JobViewer(Guid AccountId, string Name, bool WikiAdmin, List<Guid> Spaces)
{
    public bool Sees(JobRun run) => WikiAdmin || run.SpaceId is { } space && Spaces.Contains(space);

    public void RequireWikiAdmin()
    {
        if (!WikiAdmin)
        {
            throw new ApiException(403, "Tuto operaci může provést pouze správce wiki.", "forbidden");
        }
    }
}
