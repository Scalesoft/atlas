using Atlas.Server.Features.SpaceAi.Contracts;
using Atlas.Server.Features.SpaceAi.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.SpaceAi.Queries;

/// <summary>The current inclusion row of a space for the settings page. Retrieval never reads it here: SQL over the AI index
/// filters by AccessSql.SpaceInAi (migration 2026100530), which states the same default.</summary>
public static class CurrentSpaceAi
{
    public static Task<SpaceAiSetting?> LoadAsync(ISession session, Guid spaceId, CancellationToken ct)
        => session.QueryOver<SpaceAiSetting>().Where(x => x.SpaceId == spaceId && x.ActiveTo == null).SingleOrDefaultAsync(ct)!;

    public static SpaceAiSettingsView ToView(SpaceAiSetting? row)
        => new(row?.Included ?? true, row is null ? null : AdministrationVersion.Format(row.ActiveFrom));
}
