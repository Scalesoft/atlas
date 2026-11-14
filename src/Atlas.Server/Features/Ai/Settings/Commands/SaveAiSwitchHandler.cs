using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Settings.Commands;

public sealed class SaveAiSwitchHandler
{
    private readonly ISession m_session;
    private readonly AiSettingsWrite m_write;

    public SaveAiSwitchHandler(ISession session, AiSettingsWrite write)
    {
        m_session = session;
        m_write = write;
    }

    public async Task<AiSwitchView> Handle(SaveAiSwitch command, CancellationToken ct)
    {
        var actor = await m_write.BeginAsync(ct);
        var previous = await CurrentAiSettings.LoadSwitchAsync(m_session, ct);
        AdministrationVersion.Expect(previous?.ActiveFrom, command.Request.ExpectedVersion);
        var now = Temporal.NextBoundary(previous?.ActiveFrom ?? DateTime.UnixEpoch, DateTime.UtcNow);
        if (previous is not null)
        {
            previous.ActiveTo = now;
            // One current row: close it before the replacement insert reaches the database.
            await m_session.FlushAsync(ct);
        }
        var saved = new AiSetting { Id = Guid.NewGuid(), Enabled = command.Request.Enabled, ChangedById = actor.Id, ActiveFrom = now };
        await m_session.SaveAsync(saved, ct);
        m_write.Saved();
        return CurrentAiSettings.ToView(saved);
    }
}
