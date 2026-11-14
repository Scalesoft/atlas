using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Security.Domain;

namespace Atlas.Server.Features.Security.Persistence;

public sealed class ReauthChallengeMap : ClassMapping<ReauthChallenge>
{
    public ReauthChallengeMap()
    {
        Table("`ReauthChallenge`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.UserId);
        Property(x => x.SecurityStampHash, m => m.Type(NHibernateUtil.BinaryBlob));
        Property(x => x.SessionId);
        Property(x => x.Operation);
        Property(x => x.TargetKind);
        Property(x => x.TargetId);
        Property(x => x.Method);
        Property(x => x.ProviderId);
        Property(x => x.IssuedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ExpiresAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.VerifiedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ConsumedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.FailedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.Failure);
        Property(x => x.AuthTime, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.WindowClosedAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
