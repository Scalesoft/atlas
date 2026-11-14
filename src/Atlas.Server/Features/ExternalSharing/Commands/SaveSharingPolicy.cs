using Atlas.Server.Features.ExternalSharing.Contracts;
using Atlas.Server.Features.ExternalSharing.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ExternalSharing.Commands;

public sealed record SaveSharingPolicy(Guid SpaceId, SaveSharingPolicyRequest Request);
public sealed class SaveSharingPolicyHandler(ISession session, ExternalSharingAccess access)
{
    public async Task<SharingPolicyDto> Handle(SaveSharingPolicy command, CancellationToken ct)
    {
        var actor = await access.RequireSpaceAdminAsync(command.SpaceId, true, ct);
        var current = await session.QueryOver<SpaceExternalSharingPolicy>()
            .Where(x => x.SpaceId == command.SpaceId && x.ActiveTo == null).SingleOrDefaultAsync(ct);
        AdministrationVersion.Expect(current?.ActiveFrom, command.Request.ExpectedVersion);
        if ((current?.Enabled ?? false) == command.Request.Enabled)
            return new SharingPolicyDto(current?.Enabled ?? false, current is null ? null : AdministrationVersion.Format(current.ActiveFrom));
        var now = Temporal.NextBoundary(current?.ActiveFrom ?? DateTime.UnixEpoch, DateTime.UtcNow);
        if (current is not null)
        {
            current.ActiveTo = now;
            await session.FlushAsync(ct);
        }
        var policy = new SpaceExternalSharingPolicy { Id = Guid.NewGuid(), SpaceId = command.SpaceId,
            Enabled = command.Request.Enabled, ChangedById = actor.Id, ActiveFrom = now };
        await session.SaveAsync(policy, ct);
        if (!policy.Enabled)
            await ShareRevocation.SpaceAsync(session, command.SpaceId, now, actor.Id, ct);
        return new SharingPolicyDto(policy.Enabled, AdministrationVersion.Format(policy.ActiveFrom));
    }
}
