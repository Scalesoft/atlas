using FluentMigrator;
using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Comments.Domain;

namespace Atlas.Server.Features.Comments.Persistence;

[Migration(2026102790, "Comments on objects, their mentions and search documents, the space setting Komentáře")]
public sealed class CommentsMigration() : EmbeddedSchemaMigration("2026102790_comments.sql");

/// <summary>Comment of migration 2026102790; CommentMention and CommentSearchDocument are written by SQL (CommentWrites).</summary>
public sealed class CommentMap : ClassMapping<Comment>
{
    public CommentMap()
    {
        Table("`Comment`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.ObjectId);
        Property(x => x.AuthorId);
        Property(x => x.Body, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.Anchor);
        Property(x => x.CreatedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.EditedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.DeletedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.DeletedById);
    }
}
