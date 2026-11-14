using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Hub.Domain;

namespace Atlas.Server.Features.Hub.Persistence;

public sealed class HubIdentityStateMap : ClassMapping<HubIdentityState>
{
    public HubIdentityStateMap()
    {
        Table("`HubIdentityState`");
        Id(x => x.ExternalIdentityId, m => { m.Column("`ExternalIdentityId`"); m.Generator(Generators.Assigned); });
        Property(x => x.UserId);
        Property(x => x.ConnectionId);
        Property(x => x.ExternalUserId, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.SnapshotStartedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.VerifiedUntil, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.LastSuccessAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.LastAttemptAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.UpstreamAccountState);
        Property(x => x.Generation);
        Property(x => x.LastErrorCode, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.NextAttemptAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.FailureCount);
    }
}

