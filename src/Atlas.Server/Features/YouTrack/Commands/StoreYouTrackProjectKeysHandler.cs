using System.Text.Json;
using Atlas.Server.Features.YouTrack.Queries;
using Atlas.YouTrack;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.YouTrack.Commands;

public sealed class StoreYouTrackProjectKeysHandler(ISession session)
{
    public async Task Handle(StoreYouTrackProjectKeys command, CancellationToken ct)
    {
        var row = await CurrentYouTrackSettings.LoadAsync(session, ct);
        if (row is null || row.Id.ToString("N") != command.Revision)
        {
            return;
        }
        var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(row.ProjectKeysJson)!;
        var before = JsonSerializer.Serialize(keys);
        foreach (var (id, key) in command.Keys)
        {
            if (key is not null && YouTrackClient.IsProjectKey(key))
            {
                keys[id] = key;
            }
            else
            {
                keys.Remove(id);
            }
        }
        var after = JsonSerializer.Serialize(keys);
        if (after != before)
        {
            // A bulk update of this one column: a save closing the row meanwhile keeps its ActiveTo, and this write then does nothing.
            await session.CreateQuery("update YouTrackConfiguration set ProjectKeysJson = :keys where Id = :id and ActiveTo is null")
                .SetParameter("keys", after).SetParameter("id", row.Id).ExecuteUpdateAsync(ct);
        }
    }
}
