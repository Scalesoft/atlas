using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Vaults.Domain;

namespace Atlas.Server.Features.Vaults.Persistence;

public sealed class VaultLocationMap : ClassMapping<VaultLocation>
{
    public VaultLocationMap()
    {
        Table("`VaultLocation`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.Name);
        Property(x => x.Kind);
        Property(x => x.Root);
        Property(x => x.Username);
        Property(x => x.Domain);
        Property(x => x.ProtectedPassword);
        Property(x => x.ArchivedAt, m => m.Type(NHibernate.NHibernateUtil.UtcDateTime));
    }
}
