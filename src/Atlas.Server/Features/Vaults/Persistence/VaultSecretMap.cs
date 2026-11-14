using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Vaults.Domain;

namespace Atlas.Server.Features.Vaults.Persistence;

public sealed class VaultSecretMap : ClassMapping<VaultSecret>
{
    public VaultSecretMap()
    {
        Table("`VaultSecret`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.VaultId);
        Property(x => x.EntryUuid);
        Property(x => x.Title);
        Property(x => x.GroupPath);
        Property(x => x.AvailableFields);
        Property(x => x.FirstSeenAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.RemovedAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
