using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Administration.Domain;

namespace Atlas.Server.Features.Administration.Persistence;

public sealed class ProfileAccountViewMap : ClassMapping<ProfileAccountView>
{
    public ProfileAccountViewMap()
    {
        Mutable(false);
        Subselect("""
            SELECT u."Id", u."DisplayName", u."Email", r."ActiveFrom",
                EXISTS(SELECT 1 FROM "BuiltinCredential" c WHERE c."UserId"=u."Id") AS "HasBuiltinCredential",
                EXISTS(SELECT 1 FROM "ExternalIdentity" e WHERE e."UserId"=u."Id" AND e."ActiveTo" IS NULL) AS "HasExternalIdentity",
                signin."AuthenticatedAt" AS "LastSignInAt", signin."AuthenticationMethod" AS "LastSignInMethod"
            FROM "UserAccount" u
            JOIN "UserAccountRevision" r ON r."UserId"=u."Id" AND r."ActiveTo" IS NULL
            LEFT JOIN LATERAL (
                SELECT "AuthenticatedAt","AuthenticationMethod" FROM "UserSignIn"
                WHERE "UserId"=u."Id" ORDER BY "AuthenticatedAt" DESC,"Id" DESC LIMIT 1
            ) signin ON true
            """);
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.DisplayName);
        Property(x => x.Email);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.HasBuiltinCredential);
        Property(x => x.HasExternalIdentity);
        Property(x => x.LastSignInAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.LastSignInMethod);
    }
}
