using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.PersonalApi.Domain;

namespace Atlas.Server.Features.PersonalApi.Persistence;

/// <remarks>Inserted only by the migration; the application only confirms it.</remarks>
public sealed class ApiLevelsReviewMap : ClassMapping<ApiLevelsReview>
{
    public ApiLevelsReviewMap()
    {
        Table("`ApiLevelsReview`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.CreatedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.Accounts);
        Property(x => x.Tokens);
        Property(x => x.ConfirmedById);
        Property(x => x.ConfirmedAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
