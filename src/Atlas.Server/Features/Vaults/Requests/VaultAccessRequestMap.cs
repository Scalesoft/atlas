using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Features.Vaults.Requests;

public sealed class VaultAccessRequestMap : ClassMapping<VaultAccessRequest>
{
    public VaultAccessRequestMap()
    {
        Table("`VaultAccessRequest`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.VaultId);
        Property(x => x.SecretId);
        Property(x => x.VaultSpaceId);
        Property(x => x.RequesterId);
        Property(x => x.Level);
        Property(x => x.Reason);
        Property(x => x.State);
        Property(x => x.CreatedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.DecidedBy);
        Property(x => x.DecidedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.DecisionNote);
        Property(x => x.GrantId);
        Property(x => x.GrantExpiresAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
