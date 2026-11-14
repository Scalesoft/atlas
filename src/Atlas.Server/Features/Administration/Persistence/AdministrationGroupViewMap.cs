using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Administration.Domain;

namespace Atlas.Server.Features.Administration.Persistence;

public sealed class AdministrationGroupViewMap : ClassMapping<AdministrationGroupView>
{
    public AdministrationGroupViewMap()
    {
        Mutable(false);
        Subselect("""
            SELECT g."Id", g."Name", r."ActiveFrom", CASE WHEN b."GroupId" IS NULL THEN 'local' ELSE 'hub' END AS "Source",
                h."BaseUrl" AS "HubBaseUrl", b."ExternalGroupId"
            FROM "UserGroup" g
            JOIN "UserGroupRevision" r ON r."GroupId" = g."Id" AND r."ActiveTo" IS NULL
            LEFT JOIN "HubGroupBinding" b ON b."GroupId"=g."Id"
            LEFT JOIN "HubConnectionRevision" h ON h."ConnectionId"=b."ConnectionId" AND h."ActiveTo" IS NULL
            """);
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.Name);
        Property(x => x.Source);
        Property(x => x.HubBaseUrl);
        Property(x => x.ExternalGroupId);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Synchronize("UserGroup", "UserGroupRevision", "HubGroupBinding", "HubConnectionRevision");
    }
}
