using System.Text.Json;
using Atlas.Server.Features.Auth.Contracts;
using Atlas.Server.Features.Auth.Queries;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.Commands;

public sealed class SaveAiPreferencesHandler
{
    private readonly ISession m_session;
    private readonly ICurrentActor m_actor;
    private readonly IAtlasLocks m_locks;

    public SaveAiPreferencesHandler(ISession session, ICurrentActor actor, IAtlasLocks locks)
    {
        m_session = session;
        m_actor = actor;
        m_locks = locks;
    }

    public async Task<AiPreferencesView> Handle(SaveAiPreferences command, CancellationToken ct)
    {
        var actor = await m_actor.RequireAsync(ct);
        var request = command.Request;
        if (!CurrentAiPreference.Modes.Contains(request.EditorSuggestions))
        {
            throw new ApiException(400, "Neznámá hodnota návrhů při psaní.");
        }
        if (request.SearchAnswer is not null && !CurrentAiPreference.SearchAnswers.Contains(request.SearchAnswer))
        {
            throw new ApiException(400, "Neznámá hodnota odpovědi v hledání.");
        }
        if (request.AssistantScope is not null && !CurrentAiPreference.AssistantScopes.Contains(request.AssistantScope))
        {
            throw new ApiException(400, "Neznámý výchozí rozsah asistenta.");
        }
        var requested = request.DisabledSuggestionTypes ?? [];
        if (requested.Any(x => !CurrentAiPreference.SuggestionTypes.Contains(x)) || requested.Distinct().Count() != requested.Length)
        {
            throw new ApiException(400, "Neznámý nebo opakovaný typ návrhů.");
        }
        // Stored in catalogue order, so equal choices compare and serialize equally.
        var types = CurrentAiPreference.SuggestionTypes.Where(requested.Contains).ToArray();
        // Serialize only this user's preference updates; other users never wait for this lock.
        if (!await m_locks.LockAccountForUpdateAsync(actor.Id, ct))
        {
            throw new ApiException(401, "Přihlaste se.");
        }
        var current = await CurrentAiPreference.LoadAsync(m_session, actor.Id, ct);
        AdministrationVersion.Expect(current?.ActiveFrom, request.ExpectedVersion);
        // Omitted fields keep what is stored (or the defaults), so a client from before Q6 never resets them.
        var searchAnswer = request.SearchAnswer ?? current?.SearchAnswer ?? CurrentAiPreference.DefaultSearchAnswer;
        var assistantScope = request.AssistantScope ?? current?.AssistantScope ?? CurrentAiPreference.DefaultAssistantScope;
        if (current is not null && current.EditorSuggestions == request.EditorSuggestions && current.DisabledSuggestionTypes.SequenceEqual(types)
            && current.SearchAnswer == searchAnswer && current.AssistantScope == assistantScope)
        {
            return CurrentAiPreference.ToView(current);
        }
        var boundary = Temporal.NextBoundary(current?.ActiveFrom ?? DateTime.UnixEpoch, DateTime.UtcNow);
        if (current is not null)
        {
            // Close before inserting so the current index and the exclusion constraint stay valid.
            await m_session.CreateSQLQuery("""UPDATE "UserAiPreference" SET "ActiveTo"=:to WHERE "UserId"=:user AND "ActiveTo" IS NULL""")
                .SetParameter("to", boundary, NHibernateUtil.UtcDateTime).SetParameter("user", actor.Id).ExecuteUpdateAsync(ct);
        }
        await m_session.CreateSQLQuery("""
            INSERT INTO "UserAiPreference"("Id","UserId","EditorSuggestions","DisabledSuggestionTypes","ActiveFrom","SearchAnswer","AssistantScope")
            VALUES (:id,:user,:mode,CAST(:types AS jsonb),:from,:answer,:scope)
            """)
            .SetParameter("id", Guid.NewGuid()).SetParameter("user", actor.Id).SetParameter("mode", request.EditorSuggestions)
            .SetParameter("types", JsonSerializer.Serialize(types)).SetParameter("from", boundary, NHibernateUtil.UtcDateTime)
            .SetParameter("answer", searchAnswer).SetParameter("scope", assistantScope)
            .ExecuteUpdateAsync(ct);
        return CurrentAiPreference.ToView(new CurrentAiPreference.Row(request.EditorSuggestions, types, boundary, searchAnswer, assistantScope));
    }
}
