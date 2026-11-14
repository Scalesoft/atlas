using Atlas.Server.Features.Hub.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Commands;

internal static class LocalGroups
{
    public static async Task RequireAsync(ISession session, Guid groupId, CancellationToken ct)
    {
        if (await session.GetAsync<HubGroupBinding>(groupId, ct) is not null)
        {
            throw new ApiException(409, "Skupinu spravuje Hub. Členství a název měňte v Hubu, propojení v nastavení integrace Hub.",
                "external_group_managed");
        }
    }
}
