using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Features.YouTrack.Commands;
using Atlas.Server.Features.YouTrack.Contracts;
using Atlas.Server.Features.YouTrack.Domain;
using Atlas.Server.Features.YouTrack.Queries;
using Atlas.YouTrack;
using Wolverine;

namespace Atlas.Server.Features.YouTrack;

/// <summary>
/// The three reads that call the remote YouTrack (search, resolve, "Test spojení"), outside Wolverine like <c>AiAnswerRunner</c> and
/// <c>SearchEndpoints.SearchAsync</c>: a call takes up to 8 s, and docs/architecture.md section 5 keeps the YouTrack API out of an
/// open transaction. Every database step is one short transaction and the call runs between them, with no transaction and no
/// connection held. <see cref="ReadYouTrackConfiguration"/> authorizes the actor and reads the configuration, whose token stays
/// data-protected until this class unprotects it, right after that transaction and before anything else uses the configuration (a
/// token the key ring cannot read is the same 503 as ever, ahead of the "not configured" answer, the cache and the call). After the
/// call <see cref="EnsureYouTrackUnchanged"/> authorizes the actor again and fails with 409 when the configuration changed
/// meanwhile. Only then is the result cached and released, so a right revoked during the call ends in the 401, 403 or 404 the first
/// step would have given. Neither step takes the shared content advisory lock (see <see cref="YouTrackActor"/>).
/// </summary>
public sealed class YouTrackReadRunner
{
    private readonly IMessageBus m_bus;
    private readonly YouTrackClient m_client;
    private readonly YouTrackReadCache m_cache;
    private readonly IDataProtector m_protector;

    public YouTrackReadRunner(IMessageBus bus, YouTrackClient client, YouTrackReadCache cache, IDataProtectionProvider protection)
    {
        m_bus = bus;
        m_client = client;
        m_cache = cache;
        m_protector = protection.CreateProtector(SaveYouTrackSettingsHandler.TokenPurpose);
    }

    public async Task<YouTrackIssueList> SearchAsync(Guid spaceId, string query, CancellationToken ct)
    {
        var config = await ReadConfigurationAsync(spaceId, ct);
        if (!YouTrackClient.IsConfiguredFor(config.Options, spaceId))
        {
            // Omnisearch asks every searched space: one without the integration has no issues, it is not an outage.
            return new YouTrackIssueList([]);
        }
        var key = $"{config.Revision}:{spaceId:N}:search:{query}";
        var hit = m_cache.TryGet<IReadOnlyList<YouTrackIssue>>(key, out var result);
        ProjectKeys? projects = null;
        if (!hit)
        {
            result = await m_client.SearchAsync(config.Options, spaceId, query, async c => (projects ??= await ProjectKeysAsync(config, spaceId, c)).Keys, ct);
        }
        await m_bus.InvokeAsync(new EnsureYouTrackUnchanged(spaceId, config.Revision), ct);
        if (!hit)
        {
            m_cache.Set(key, result!);
            await CacheProjectKeysAsync(config, spaceId, projects, ct);
        }
        return new YouTrackIssueList(result!);
    }

    public async Task<YouTrackIssueResult> ResolveAsync(Guid spaceId, string reference, CancellationToken ct)
    {
        return new YouTrackIssueResult((await ResolveManyAsync(spaceId, [reference], ct))[0]);
    }

    /// <summary>The issues the references name, in their order (null for one that is not in the space's projects), with one
    /// authorization before and one after a single YouTrack call for all that are not cached. A miss YouTrack answered is cached too,
    /// under the issue key, so the same issue spelled differently or a key that names nothing is not asked again. A key the batch did
    /// not ask about (no allowed project's prefix) is null here but not cached, so resolving it alone still asks YouTrack.</summary>
    public async Task<IReadOnlyList<YouTrackIssue?>> ResolveManyAsync(Guid spaceId, IReadOnlyList<string> references, CancellationToken ct)
    {
        var config = await ReadConfigurationAsync(spaceId, ct);
        var keys = references.Select(reference => YouTrackClient.IssueKey(config.Options, reference)).ToArray();
        string CacheKey(string key) => $"{config.Revision}:{spaceId:N}:resolve:{key}";
        var found = new Dictionary<string, YouTrackIssue?>();
        var misses = new List<string>();
        foreach (var key in keys.Distinct())
        {
            if (m_cache.TryGet<YouTrackIssue?>(CacheKey(key), out var issue))
            {
                found[key] = issue;
            }
            else
            {
                misses.Add(key);
            }
        }
        IReadOnlyDictionary<string, YouTrackIssue?> answered = new Dictionary<string, YouTrackIssue?>();
        ProjectKeys? projects = null;
        if (misses.Count == 1)
        {
            // One issue is read by its ID, without the project lookup a search needs.
            answered = new Dictionary<string, YouTrackIssue?> { [misses[0]] = await m_client.ResolveAsync(config.Options, spaceId, misses[0], ct) };
        }
        else if (misses.Count > 1)
        {
            projects = await ProjectKeysAsync(config, spaceId, ct);
            answered = await m_client.ResolveManyAsync(config.Options, spaceId, projects.Keys, misses, ct);
        }
        await m_bus.InvokeAsync(new EnsureYouTrackUnchanged(spaceId, config.Revision), ct);
        await CacheProjectKeysAsync(config, spaceId, projects, ct);
        foreach (var key in misses)
        {
            if (answered.TryGetValue(key, out var issue))
            {
                m_cache.Set(CacheKey(key), issue);
            }
            found[key] = issue;
        }
        return keys.Select(key => found[key]).ToArray();
    }

    public async Task<YouTrackTestResult> TestConnectionAsync(CancellationToken ct)
    {
        var config = await ReadConfigurationAsync(null, ct);
        var result = await m_client.TestConnectionAsync(config.Options, ct);
        await m_bus.InvokeAsync(new EnsureYouTrackUnchanged(null, config.Revision), ct);
        // The settings form lists these projects; storing their short names here lets the next save carry them for the lexicon.
        await m_bus.InvokeAsync(new StoreYouTrackProjectKeys(config.Revision, result.DistinctBy(p => p.Id).ToDictionary(p => p.Id, p => (string?)p.ShortName)), ct);
        return new YouTrackTestResult(result);
    }

    /// <summary>The allowed projects' short names, read from YouTrack once per configuration revision and space while the cache keeps
    /// them, not once per query: typing in the picker sends one request per query, not one more per project.</summary>
    private async Task<ProjectKeys> ProjectKeysAsync(YouTrackConfigurationSnapshot config, Guid spaceId, CancellationToken ct)
    {
        if (m_cache.TryGet<IReadOnlyList<string>>(ProjectKeysCacheKey(config, spaceId), out var keys))
        {
            return new ProjectKeys(keys!, null);
        }
        var read = await m_client.ProjectShortNamesAsync(config.Options, spaceId, ct);
        return new ProjectKeys(read.Values.ToArray(), read);
    }

    /// <summary>Caches short names just read, after the second authorization like every other answer, and stores what changed for the
    /// place lexicon (a project the answer lacks is gone).</summary>
    private async Task CacheProjectKeysAsync(YouTrackConfigurationSnapshot config, Guid spaceId, ProjectKeys? projects, CancellationToken ct)
    {
        if (projects?.Read is { } read)
        {
            m_cache.Set(ProjectKeysCacheKey(config, spaceId), projects.Keys);
            await m_bus.InvokeAsync(new StoreYouTrackProjectKeys(config.Revision,
                config.Options.SpaceProjects[spaceId].ToDictionary(id => id, id => read.GetValueOrDefault(id))), ct);
        }
    }

    private static string ProjectKeysCacheKey(YouTrackConfigurationSnapshot config, Guid spaceId) => $"{config.Revision}:{spaceId:N}:projects";

    /// <summary><c>Read</c>: the names by project ID when YouTrack was just asked, null for a cache hit.</summary>
    private sealed record ProjectKeys(IReadOnlyList<string> Keys, IReadOnlyDictionary<string, string>? Read);

    private async Task<YouTrackConfigurationSnapshot> ReadConfigurationAsync(Guid? spaceId, CancellationToken ct)
    {
        var stored = await m_bus.InvokeAsync<YouTrackProtectedConfiguration>(new ReadYouTrackConfiguration(spaceId), ct);
        return stored.Unprotect(m_protector);
    }
}
