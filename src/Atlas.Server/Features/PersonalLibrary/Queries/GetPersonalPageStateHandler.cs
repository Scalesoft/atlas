using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary.Domain;

namespace Atlas.Server.Features.PersonalLibrary.Queries;

public sealed class GetPersonalPageStateHandler
{
    private readonly PersonalPageGuard m_guard;

    public GetPersonalPageStateHandler(PersonalPageGuard guard)
    {
        m_guard = guard;
    }

    public async Task<PersonalPageState> Handle(GetPersonalPageState query, CancellationToken ct)
    {
        var actor = await m_guard.RequirePageAsync(query.PageId, false, ct);
        return await m_guard.ReadStateAsync(query.PageId, actor.Id, ct);
    }
}
