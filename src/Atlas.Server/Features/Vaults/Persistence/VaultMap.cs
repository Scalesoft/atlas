using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Vaults.Domain;

namespace Atlas.Server.Features.Vaults.Persistence;

public sealed class VaultMap : ClassMapping<Vault>
{
    public VaultMap()
    {
        Table("`Vault`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.SpaceId);
        Property(x => x.Name);
        Property(x => x.LocationId);
        Property(x => x.FileName);
        Property(x => x.ProtectedKdbxPassword);
        Property(x => x.State);
        Property(x => x.LastFailure);
        Property(x => x.LastSnapshotSha256, m => m.Type(NHibernateUtil.BinaryBlob));
        Property(x => x.LastRefreshAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.CreatedBy);
        Property(x => x.CreatedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ArchivedAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
