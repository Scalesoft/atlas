using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Settings.Commands;

public sealed class SaveAiFeaturesHandler
{
    private readonly ISession m_session;
    private readonly AiSettingsWrite m_write;

    public SaveAiFeaturesHandler(ISession session, AiSettingsWrite write)
    {
        m_session = session;
        m_write = write;
    }

    public async Task<AiFeaturesView> Handle(SaveAiFeatures command, CancellationToken ct)
    {
        var actor = await m_write.BeginAsync(ct);
        var items = command.Request.Items;
        if (items is null || items.Count > AiTaskCatalog.All.Count
            || items.Select(x => x?.Task).Distinct(StringComparer.Ordinal).Count() != items.Count)
        {
            throw new ApiException(400, "Každá úloha může být v požadavku nejvýše jednou.");
        }
        foreach (var item in items)
        {
            var task = AiTaskCatalog.Find(item?.Task);
            if (task is not { Switchable: true })
            {
                throw new ApiException(400, "Neznámá nebo nepřepínatelná AI úloha.");
            }
            if (item!.CloudWorkingText is not null && task.Input != AiInputClass.WorkingText)
            {
                throw new ApiException(400, "Povolení cloudu se týká jen úloh nad rozepsaným textem.");
            }
            if (item.DailyLimit is { } limit && (!task.Limitable || limit is < 1 or > AiTaskCatalog.MaxDailyLimit))
            {
                throw new ApiException(400, task.Limitable
                    ? "Denní limit musí být mezi 1 a 10 000, nebo prázdný."
                    : "Denní limit lze nastavit jen u odpovědi z wiki a asistenta.");
            }
        }
        var previous = await CurrentAiSettings.LoadFeaturesAsync(m_session, ct);
        AdministrationVersion.Expect(previous?.ActiveFrom, command.Request.ExpectedVersion);
        var tasks = CurrentAiSettings.ResolveTasks(previous?.TasksJson).ToDictionary(StringComparer.Ordinal);
        foreach (var item in items)
        {
            tasks[item.Task] = new AiTaskSwitch(item.Enabled, item.CloudWorkingText ?? tasks[item.Task].CloudWorkingText, item.DailyLimit);
        }
        var now = Temporal.NextBoundary(previous?.ActiveFrom ?? DateTime.UnixEpoch, DateTime.UtcNow);
        if (previous is not null)
        {
            previous.ActiveTo = now;
            await m_session.FlushAsync(ct);
        }
        var saved = new AiFeatureSettings { Id = Guid.NewGuid(), TasksJson = CurrentAiSettings.SerializeTasks(tasks), ChangedById = actor.Id, ActiveFrom = now };
        await m_session.SaveAsync(saved, ct);
        m_write.Saved();
        return CurrentAiSettings.ToView(saved);
    }
}
