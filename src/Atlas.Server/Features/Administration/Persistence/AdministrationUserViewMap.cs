using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Administration.Domain;

namespace Atlas.Server.Features.Administration.Persistence;

public sealed class AdministrationUserViewMap : ClassMapping<AdministrationUserView>
{
    public AdministrationUserViewMap()
    {
        Mutable(false);
        Subselect("""
            SELECT u."Id", u."Username", u."DisplayName", u."IsActive", u."IsWikiAdmin", r."ActiveFrom",
                c."UserId" IS NOT NULL AS "HasBuiltinCredential",
                c."PasswordHash" IS NOT NULL AND length(c."PasswordHash") > 0 AS "HasPassword"
            FROM "UserAccount" u
            JOIN "UserAccountRevision" r ON r."UserId" = u."Id" AND r."ActiveTo" IS NULL
            LEFT JOIN "BuiltinCredential" c ON c."UserId" = u."Id"
            """);
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.Username);
        Property(x => x.DisplayName);
        Property(x => x.IsActive);
        Property(x => x.IsWikiAdmin);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.HasBuiltinCredential);
        Property(x => x.HasPassword);
    }
}
