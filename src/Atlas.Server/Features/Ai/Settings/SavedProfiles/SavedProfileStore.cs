using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Settings.SavedProfiles;

/// <summary>Library reads and close-and-insert writes; callers authorize and hold the AI settings lock for writes.</summary>
public sealed class SavedProfileStore(ISession session)
{
    public async Task<AiSavedProfile> RequireAsync(string kind, Guid id, CancellationToken ct)
        => await session.QueryOver<AiSavedProfile>()
            .Where(x => x.Kind == kind && x.ProfileId == id && x.ActiveTo == null).SingleOrDefaultAsync(ct)
            ?? throw new ApiException(404, "Uložený profil AI neexistuje.");

    public async Task<AiProfileRevision> RevisionAsync(AiSavedProfile row, CancellationToken ct)
        => await session.GetAsync<AiProfileRevision>(row.RevisionId, ct)
            ?? throw new ApiException(404, "Revize profilu AI neexistuje.");

    public async Task<SavedProfileList> ListAsync(string kind, CancellationToken ct)
    {
        var assignment = await CurrentAiSettings.LoadAssignmentAsync(session, kind, ct);
        var rows = await session.QueryOver<AiSavedProfile>().Where(x => x.Kind == kind && x.ActiveTo == null)
            .OrderBy(x => x.Name).Asc.ListAsync(ct);
        if (rows.Count == 0) return new([], assignment is null ? null : AdministrationVersion.Format(assignment.ActiveFrom));
        var ids = rows.Select(x => x.RevisionId).Distinct().ToArray();
        var revisions = (await session.QueryOver<AiProfileRevision>().WhereRestrictionOn(x => x.Id).IsIn(ids).ListAsync(ct))
            .ToDictionary(x => x.Id);
        return new(rows.Select(row => View(row, revisions[row.RevisionId], assignment)).ToArray(),
            assignment is null ? null : AdministrationVersion.Format(assignment.ActiveFrom));
    }

    public static SavedProfileView View(AiSavedProfile row, AiProfileRevision revision, AiProfileAssignment? assignment)
    {
        var version = AdministrationVersion.Format(row.ActiveFrom);
        // The editor's CAS is the library version; editing a library model is never locked by the live index.
        var profile = CurrentAiSettings.ToView(row.Kind,
            new AiProfileAssignment { ActiveFrom = row.ActiveFrom, RevisionId = revision.Id }, revision, false);
        return new(row.ProfileId, row.Name, version, profile, assignment?.RevisionId == revision.Id);
    }

    public async Task<string> RequireNameAsync(string? value, string kind, Guid? exceptId, CancellationToken ct)
    {
        var name = value?.Trim() ?? "";
        if (name.Length is < 1 or > 200 || name.Any(char.IsControl))
            throw new ApiException(400, "Název profilu musí mít 1 až 200 znaků bez řídicích znaků.");
        var duplicate = await session.CreateSQLQuery("""
            SELECT count(*) FROM "AiSavedProfile"
            WHERE "Kind"=:kind AND "ActiveTo" IS NULL AND lower("Name")=lower(:name) AND "ProfileId"<>:except
            """).SetParameter("kind", kind).SetParameter("name", name).SetParameter("except", exceptId ?? Guid.Empty)
            .UniqueResultAsync<long>(ct);
        if (duplicate > 0) throw new ApiException(409, "Profil s tímto názvem už existuje.", "ai_profile_name_exists");
        return name;
    }

    public async Task<AiSavedProfile> AppendAsync(AiSavedProfile? previous, string kind, string name, Guid revisionId,
        Guid actorId, CancellationToken ct)
    {
        var now = Temporal.NextBoundary(previous?.ActiveFrom ?? DateTime.UnixEpoch, DateTime.UtcNow);
        if (previous is not null)
        {
            previous.ActiveTo = now;
            await session.FlushAsync(ct);
        }
        var row = new AiSavedProfile
        {
            Id = Guid.CreateVersion7(), ProfileId = previous?.ProfileId ?? Guid.CreateVersion7(), Kind = kind,
            Name = name, RevisionId = revisionId, ChangedById = actorId, ActiveFrom = now,
        };
        await session.SaveAsync(row, ct);
        return row;
    }

    public async Task<bool> IsActiveLineageAsync(AiSavedProfile row, AiProfileAssignment? assignment, CancellationToken ct)
        => assignment is not null && await session.QueryOver<AiSavedProfile>()
            .Where(x => x.ProfileId == row.ProfileId && x.RevisionId == assignment.RevisionId).RowCountAsync(ct) > 0;
}
