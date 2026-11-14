using NHibernate;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Collaboration.Domain;

namespace Atlas.Server.Features.Collaboration.Persistence;

public sealed class CollaborationUpdateMap : ClassMapping<CollaborationUpdate>
{
    public CollaborationUpdateMap()
    {
        Table("`CollaborationUpdate`");
        ComposedId(id =>
        {
            id.Property(x => x.PageId, m => m.Column("`PageId`"));
            id.Property(x => x.Generation, m => m.Column("`Generation`"));
            id.Property(x => x.Sequence, m => m.Column("`Sequence`"));
        });
        Property(x => x.ClientUpdateId);
        Property(x => x.UserId);
        Property(x => x.ReceivedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.Payload, m => m.Type(NHibernateUtil.BinaryBlob));
        Property(x => x.Channel);
        Property(x => x.ApiTokenId);
        Property(x => x.AiConversationId);
    }
}
