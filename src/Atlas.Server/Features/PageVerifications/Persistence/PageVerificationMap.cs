using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

using Atlas.Server.Features.PageVerifications.Domain;

namespace Atlas.Server.Features.PageVerifications.Persistence;

public sealed class PageVerificationMap : ClassMapping<PageVerification>
{
    public PageVerificationMap()
    {
        Table("`PageVerification`");
        DynamicUpdate(true);
        Id(x => x.PageId, m => { m.Generator(Generators.Assigned); m.Column("`PageId`"); });
        Property(x => x.StewardId);
        Property(x => x.IntervalMonths);
        Property(x => x.VerifiedById);
        Property(x => x.VerifiedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.VerifiedUntil, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.VerifiedVersion);
        Property(x => x.VerifiedChannel, m => m.Length(16));
        Property(x => x.NoticeStage);
    }
}
