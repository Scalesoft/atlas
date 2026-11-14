using Atlas.Server.Features.ExternalSharing.Contracts;
using Atlas.Server.Features.ExternalSharing.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ExternalSharing.Queries;

public sealed record GetSharingPolicy(Guid SpaceId);
public sealed class GetSharingPolicyHandler(ISession session, ExternalSharingAccess access)
{
    public async Task<SharingPolicyDto> Handle(GetSharingPolicy query, CancellationToken ct)
    {
        await access.RequireSpaceAdminAsync(query.SpaceId, false, ct);
        var policy = await session.QueryOver<SpaceExternalSharingPolicy>()
            .Where(x => x.SpaceId == query.SpaceId && x.ActiveTo == null).SingleOrDefaultAsync(ct);
        return new SharingPolicyDto(policy?.Enabled ?? false, policy is null ? null : AdministrationVersion.Format(policy.ActiveFrom));
    }
}
