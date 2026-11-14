using System.Data;
using System.Data.Common;
using System.Text;
using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Features.Pages.Live;
using Atlas.Server.Features.TreeReferences;
using NHibernate.Engine;
using NHibernate.Event;
using NHibernate.Persister.Entity;

namespace Atlas.Server.Platform.Persistence;

[FluentMigrator.Migration(2026110930, "The triggers of pages, revisions, boards, tree references and spaces go: C# writes what they derived")]
public sealed class PageHousekeepingTriggersMigration() : EmbeddedSchemaMigration("2026110930_page_housekeeping_triggers.sql");

/// <summary>
/// What the rows of pages, revisions, boards, tree references and spaces bring along, written by NHibernate right after the row in the
/// same transaction (the triggers of migrations 011–2026110630 did it until 2026110930): the content envelope (ContentResource), the
/// tree's history and concurrency token (PageOrganizationRevision, SpaceOrganization), a space's metadata history, each author's newest
/// revision (PageContribution) and the live page channel (<see cref="PageChangeListener.Channel"/>: NOTIFY is delivered only when the
/// transaction commits, once per distinct payload, so a rolled back write announces nothing). The one place every entity writer passes
/// through; the history reads the row as written, so no value is converted here. Bare SQL that writes these columns does the same in its
/// own statement (BoardContent, TreeReferenceContent, BoardPurge, ObjectPurge).
/// </summary>
/// <remarks>
/// Before a page's insert it takes its space FOR UPDATE, checks the parent and gives a page without a place the end of its level: the
/// lock serialises the creates of a space, so two never get the same place or a parent trashed meanwhile. One round trip per inserted or
/// deleted row that changes something here (a row inserted later in the same flush may reference its envelope); the effects of the
/// updated rows and of inserted revisions go in one round trip at the end of the flush, so a bulk update (the space trash and restore)
/// costs one, not one per row.
/// </remarks>
public sealed class ContentRowEffects : IPreInsertEventListener, IPostInsertEventListener, IPostUpdateEventListener, IPostDeleteEventListener,
    IFlushEventListener, IAutoFlushEventListener
{
    public static readonly ContentRowEffects Instance = new();

    /// <summary>The effects of the rows a flush updated, run when it ends (<see cref="OnFlush"/>, <see cref="OnAutoFlush"/>).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ISessionImplementor, List<Effect>> Pending = new();

    private static readonly string[] PageOrganization = ["SpaceId", "ParentId", "SortOrder", "IsTrashed", "TrashedAt", "TrashedById", "TrashBatchId"];
    private static readonly string[] TreeOrganization = ["SpaceId", "ParentPageId", "SortOrder", "IsTrashed"];
    private static readonly string[] SpaceMetadata = ["Name", "Icon", "Color", "Description", "PinPolicy", "CommentsEnabled", "PublicationMode"];

    private const string PageColumns = "\"SpaceId\",\"ParentId\",\"SortOrder\",\"IsTrashed\",\"TrashedAt\",\"TrashedById\",\"TrashBatchId\"";
    private const string SpaceColumns = "\"Name\",\"Icon\",\"Color\",\"Description\",\"PinPolicy\",\"CommentsEnabled\",\"PublicationMode\"";

    /// <summary>The page envelope as the row is: a page's version is its save version, its author the owner.</summary>
    private const string PageEnvelopeInsert = """
        INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","Version","CreatedAt","CreatedBy","UpdatedAt","TrashedAt")
        SELECT "Id",'atlas.page','space',"SpaceId","Version",now(),"OwnerId",now(),"TrashedAt" FROM "Page" WHERE "Id"=@id;
        """;

    /// <summary>A board's envelope as the row is: its sequence is the version, and it carries the title (RelationNodes, the lists).</summary>
    private const string BoardEnvelopeInsert = """
        INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","Version","CreatedAt","CreatedBy","UpdatedAt","TrashedAt","Title")
        SELECT "Id",'atlas.board','space',"SpaceId","Sequence",now(),"CreatedBy",now(),"TrashedAt","Title" FROM "Board" WHERE "Id"=@id;
        """;

    private const string SpaceOrganizationBump = """UPDATE "SpaceOrganization" SET "Version"="Version"+1 WHERE "SpaceId" IN (@space,@old);""";

    private ContentRowEffects()
    {
    }

    public bool OnPreInsert(PreInsertEvent @event)
    {
        if (@event.Entity is Page page)
        {
            @event.Session.Batcher.ExecuteBatch();
            using var command = Prepare(@event.Session, page, @event);
            try
            {
                using var reader = command.ExecuteReader();
                Place(reader, page, @event, reader.NextResult() && reader.Read());
            }
            catch
            {
                PreparedStatements.Forget(command);
                throw;
            }
        }
        return false;
    }

    public async Task<bool> OnPreInsertAsync(PreInsertEvent @event, CancellationToken cancellationToken)
    {
        if (@event.Entity is Page page)
        {
            await @event.Session.Batcher.ExecuteBatchAsync(cancellationToken);
            await using var command = Prepare(@event.Session, page, @event);
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                Place(reader, page, @event, await reader.NextResultAsync(cancellationToken) && await reader.ReadAsync(cancellationToken));
            }
            catch
            {
                await PreparedStatements.ForgetAsync(command);
                throw;
            }
        }
        return false;
    }

    public void OnPostInsert(PostInsertEvent @event)
    {
        if (!Deferred(@event))
        {
            Execute(@event.Session, Inserted(@event.Entity, @event.Session));
        }
    }

    public Task OnPostInsertAsync(PostInsertEvent @event, CancellationToken cancellationToken)
        => Deferred(@event) ? Task.CompletedTask : ExecuteAsync(@event.Session, Inserted(@event.Entity, @event.Session), cancellationToken);

    /// <summary>A revision's contribution and notification: no row of its flush references them, so they join the update effects at
    /// its end (a save sends them with the page's envelope, one round trip).</summary>
    private static bool Deferred(PostInsertEvent @event)
    {
        if (@event.Entity is not PageRevision)
        {
            return false;
        }
        Pending.GetOrCreateValue(@event.Session).Add(Inserted(@event.Entity, @event.Session)!);
        return true;
    }

    public void OnPostUpdate(PostUpdateEvent @event)
    {
        if (Updated(@event) is { } effect)
        {
            Pending.GetOrCreateValue(@event.Session).Add(effect);
        }
    }

    public Task OnPostUpdateAsync(PostUpdateEvent @event, CancellationToken cancellationToken)
    {
        OnPostUpdate(@event);
        return Task.CompletedTask;
    }

    // Appended after NHibernate's own flush listeners: the rows are written, their update effects follow before anything reads them.
    public void OnFlush(FlushEvent @event) => Execute(@event.Session, TakePending(@event.Session));

    public Task OnFlushAsync(FlushEvent @event, CancellationToken cancellationToken) => ExecuteAsync(@event.Session, TakePending(@event.Session), cancellationToken);

    public void OnAutoFlush(AutoFlushEvent @event) => Execute(@event.Session, TakePending(@event.Session));

    public Task OnAutoFlushAsync(AutoFlushEvent @event, CancellationToken cancellationToken) => ExecuteAsync(@event.Session, TakePending(@event.Session), cancellationToken);

    /// <summary>The pending update effects as one command, each one's parameters renamed apart (<c>@id</c> of the third is <c>@id_2</c>).</summary>
    private static Effect? TakePending(ISessionImplementor session)
    {
        if (!Pending.TryGetValue(session, out var effects) || effects.Count == 0)
        {
            return null;
        }
        Pending.Remove(session);
        if (effects.Count == 1)
        {
            return effects[0];
        }
        var sql = new StringBuilder();
        var parameters = new List<(string Name, object? Value)>();
        for (var index = 0; index < effects.Count; index++)
        {
            sql.Append(System.Text.RegularExpressions.Regex.Replace(effects[index].Sql, @"@(\w+)", $"@$1_{index}"));
            parameters.AddRange(effects[index].Parameters.Select(parameter => ($"{parameter.Name}_{index}", parameter.Value)));
        }
        return new Effect(sql.ToString(), [.. parameters]);
    }

    public void OnPostDelete(PostDeleteEvent @event) => Execute(@event.Session, Deleted(@event.Entity));

    public Task OnPostDeleteAsync(PostDeleteEvent @event, CancellationToken cancellationToken)
        => ExecuteAsync(@event.Session, Deleted(@event.Entity), cancellationToken);

    /// <summary>The space locked first, then (a statement of its own, so it reads after the lock) the parent and the end of the level.</summary>
    private static DbCommand Prepare(ISessionImplementor session, Page page, PreInsertEvent @event)
    {
        var sortOrder = (int)@event.State[Index(@event.Persister, "SortOrder")];
        var command = Command(session, $"""
            SELECT 1 FROM "Space" WHERE "Id"=@space FOR UPDATE;
            SELECT @parent IS NULL OR EXISTS(SELECT 1 FROM "Page" WHERE "Id"=@parent AND "SpaceId"=@space AND NOT "IsTrashed"),
              {(sortOrder == 0
                  ? "(SELECT COALESCE(max(\"SortOrder\"),0) FROM \"Page\" WHERE \"SpaceId\"=@space AND \"ParentId\" IS NOT DISTINCT FROM @parent AND NOT \"IsTrashed\")"
                  : "0")};
            """, ("space", page.SpaceId), ("parent", page.ParentId));
        return command;
    }

    private static void Place(DbDataReader reader, Page page, PreInsertEvent @event, bool read)
    {
        if (!read || !reader.GetBoolean(0))
        {
            throw new InvalidOperationException("Parent must be active in the same space");
        }
        var index = Index(@event.Persister, "SortOrder");
        if ((int)@event.State[index] != 0)
        {
            return;
        }
        var tail = Convert.ToInt64(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture);
        if (tail > 2147482623)
        {
            throw new InvalidOperationException("Sibling order requires normalization");
        }
        page.SortOrder = (int)tail + 1024;
        @event.State[index] = page.SortOrder;
    }

    private static Effect? Inserted(object entity, ISessionImplementor session)
    {
        return entity switch
        {
            PageRevision revision => new Effect($"""
                INSERT INTO "PageContribution"("UserId","PageId","LastRevisionVersion") VALUES(@author,@page,@version)
                  ON CONFLICT("UserId","PageId") DO UPDATE SET "LastRevisionVersion"=greatest("PageContribution"."LastRevisionVersion",EXCLUDED."LastRevisionVersion");
                SELECT pg_notify('{PageChangeListener.Channel}',@payload);
                """, ("author", revision.AuthorId), ("page", revision.PageId), ("version", revision.Version), ("payload", $"{revision.PageId}:working:{revision.Title}")),
            Page page => new Effect(PageEnvelopeInsert + $"""
                INSERT INTO "PageOrganizationRevision"("Id","PageId",{PageColumns},"ActorId","ActiveFrom")
                SELECT gen_random_uuid(),"Id",{PageColumns},@actor,clock_timestamp() FROM "Page" WHERE "Id"=@id;
                """ + SpaceOrganizationBump, ("id", page.Id), ("space", page.SpaceId), ("old", page.SpaceId), ("actor", TransactionActor.Of(session))),
            Board board => new Effect(BoardEnvelopeInsert + SpaceOrganizationBump, ("id", board.Id), ("space", board.SpaceId), ("old", board.SpaceId)),
            TreeReference reference => new Effect(SpaceOrganizationBump, ("space", reference.SpaceId), ("old", reference.SpaceId)),
            Features.Spaces.Domain.Space space => new Effect($"""
                INSERT INTO "SpaceOrganization"("SpaceId") VALUES(@id);
                INSERT INTO "SpaceMetadataRevision"("Id","SpaceId",{SpaceColumns},"ChangedBy","ActiveFrom")
                SELECT gen_random_uuid(),"Id",{SpaceColumns},@actor,clock_timestamp() FROM "Space" WHERE "Id"=@id;
                """, ("id", space.Id), ("actor", TransactionActor.Of(session))),
            _ => null
        };
    }

    private static Effect? Updated(PostUpdateEvent @event)
    {
        var sql = new StringBuilder();
        switch (@event.Entity)
        {
            case Page page:
                var old = (Guid)Old(@event, "SpaceId")!;
                if (Changed(@event, "SpaceId", "Version", "TrashedAt"))
                {
                    sql.Append($"""
                        UPDATE "ContentResource" r SET "SpaceId"=p."SpaceId","Version"=p."Version","TrashedAt"=p."TrashedAt"{(Changed(@event, "Version") ? ",\"UpdatedAt\"=now()" : "")}
                        FROM "Page" p WHERE r."Id"=@id AND p."Id"=@id;
                        """);
                }
                if (Changed(@event, PageOrganization))
                {
                    sql.Append($"""
                        WITH closed AS (
                          UPDATE "PageOrganizationRevision" SET "ActiveTo"=greatest(clock_timestamp(),"ActiveFrom"+interval '1 microsecond')
                          WHERE "PageId"=@id AND "ActiveTo" IS NULL RETURNING "ActiveTo")
                        INSERT INTO "PageOrganizationRevision"("Id","PageId",{PageColumns},"ActorId","ActiveFrom")
                        SELECT gen_random_uuid(),"Id",{PageColumns},@actor,coalesce((SELECT "ActiveTo" FROM closed),clock_timestamp()) FROM "Page" WHERE "Id"=@id;
                        """ + SpaceOrganizationBump);
                }
                // What an open page shows besides its text: its publication, the trash, its place (the breadcrumb) and its mode (Živě).
                if (Changed(@event, "PublishedRevisionId", "IsTrashed", "ParentId", "SpaceId", "PublicationMode"))
                {
                    sql.Append($"SELECT pg_notify('{PageChangeListener.Channel}',@payload);");
                }
                return sql.Length == 0 ? null : new Effect(sql.ToString(), ("id", page.Id), ("space", page.SpaceId), ("old", old),
                    ("actor", TransactionActor.Of(@event.Session)), ("payload", page.Id + ":published"));
            case Board board:
                if (Changed(@event, "SpaceId", "Sequence", "TrashedAt", "Title"))
                {
                    sql.Append($"""
                        UPDATE "ContentResource" r SET "SpaceId"=b."SpaceId","Version"=b."Sequence","TrashedAt"=b."TrashedAt","Title"=b."Title"{(Changed(@event, "Sequence") ? ",\"UpdatedAt\"=now()" : "")}
                        FROM "Board" b WHERE r."Id"=@id AND b."Id"=@id;
                        """);
                }
                if (Changed(@event, TreeOrganization))
                {
                    sql.Append(SpaceOrganizationBump);
                }
                return sql.Length == 0 ? null : new Effect(sql.ToString(), ("id", board.Id), ("space", board.SpaceId), ("old", (Guid)Old(@event, "SpaceId")!));
            case TreeReference reference:
                return Changed(@event, TreeOrganization) ? new Effect(SpaceOrganizationBump, ("space", reference.SpaceId), ("old", (Guid)Old(@event, "SpaceId")!)) : null;
            case Features.Spaces.Domain.Space space:
                return Changed(@event, SpaceMetadata) ? new Effect($"""
                    WITH closed AS (
                      UPDATE "SpaceMetadataRevision" SET "ActiveTo"=greatest(clock_timestamp(),"ActiveFrom"+interval '1 microsecond')
                      WHERE "SpaceId"=@id AND "ActiveTo" IS NULL RETURNING "ActiveTo")
                    INSERT INTO "SpaceMetadataRevision"("Id","SpaceId",{SpaceColumns},"ChangedBy","ActiveFrom")
                    SELECT gen_random_uuid(),"Id",{SpaceColumns},@actor,coalesce((SELECT "ActiveTo" FROM closed),clock_timestamp()) FROM "Space" WHERE "Id"=@id;
                    """, ("id", space.Id), ("actor", TransactionActor.Of(@event.Session))) : null;
            default:
                return null;
        }
    }

    private static Effect? Deleted(object entity)
    {
        return entity switch
        {
            Page page => new Effect("""DELETE FROM "ContentResource" WHERE "Id"=@id;""", ("id", page.Id)),
            Board board => new Effect("""DELETE FROM "ContentResource" WHERE "Id"=@id;""", ("id", board.Id)),
            TreeReference reference => new Effect(SpaceOrganizationBump, ("space", reference.SpaceId), ("old", reference.SpaceId)),
            _ => null
        };
    }

    /// <summary>Whether a property changed; a row updated without its loaded state (a detached update) counts as changed.</summary>
    private static bool Changed(PostUpdateEvent @event, params string[] properties)
    {
        return @event.OldState is null || properties.Any(name => !Equals(@event.OldState[Index(@event.Persister, name)], @event.State[Index(@event.Persister, name)]));
    }

    private static object? Old(PostUpdateEvent @event, string property)
    {
        return (@event.OldState ?? @event.State)[Index(@event.Persister, property)];
    }

    private static int Index(IEntityPersister persister, string property)
    {
        return Array.IndexOf(persister.PropertyNames, property);
    }

    private static void Execute(ISessionImplementor session, Effect? effect)
    {
        if (effect is not null)
        {
            // The statements read the rows NHibernate wrote: a batch it still holds (adonet.batch_size) goes to the server first.
            session.Batcher.ExecuteBatch();
            using var command = Command(session, effect.Sql, effect.Parameters);
            try
            {
                command.ExecuteNonQuery();
            }
            catch
            {
                PreparedStatements.Forget(command);
                throw;
            }
        }
    }

    private static async Task ExecuteAsync(ISessionImplementor session, Effect? effect, CancellationToken ct)
    {
        if (effect is not null)
        {
            // The statements read the rows NHibernate wrote: a batch it still holds (adonet.batch_size) goes to the server first.
            await session.Batcher.ExecuteBatchAsync(ct);
            await using var command = Command(session, effect.Sql, effect.Parameters);
            try
            {
                await command.ExecuteNonQueryAsync(ct);
            }
            catch
            {
                await PreparedStatements.ForgetAsync(command);
                throw;
            }
        }
    }

    private static DbCommand Command(ISessionImplementor session, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = session.Connection.CreateCommand();
        session.ConnectionManager.EnlistInTransaction(command);
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.DbType = value switch { string => DbType.String, int => DbType.Int32, _ => DbType.Guid };
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private sealed record Effect(string Sql, params (string Name, object? Value)[] Parameters);
}
