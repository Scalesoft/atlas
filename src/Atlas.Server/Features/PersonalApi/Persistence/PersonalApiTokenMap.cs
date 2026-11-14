using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.PersonalApi.Domain;

namespace Atlas.Server.Features.PersonalApi.Persistence;

/// <remarks>Immutable credential metadata plus revocation: only ActiveTo ever changes, so the table needs no
/// immutability trigger and the mapping stays mutable for that one write.</remarks>
public sealed class PersonalApiTokenMap : ClassMapping<PersonalApiToken>
{
    public PersonalApiTokenMap()
    {
        Table("`PersonalApiToken`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.UserId);
        Property(x => x.Name);
        Property(x => x.TokenHash);
        Property(x => x.Prefix);
        Property(x => x.SecurityStamp);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ExpiresAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Set(x => x.Scopes, collection =>
        {
            collection.Table("`PersonalApiTokenScope`");
            collection.Key(key => key.Column("`TokenId`"));
            collection.Lazy(CollectionLazy.Lazy);
        }, element => element.Element(property => property.Column("`Scope`")));
        Set(x => x.Channels, collection =>
        {
            collection.Table("`PersonalApiTokenChannel`");
            collection.Key(key => key.Column("`TokenId`"));
            collection.Lazy(CollectionLazy.Lazy);
        }, element => element.Element(property => property.Column("`Channel`")));
    }
}
