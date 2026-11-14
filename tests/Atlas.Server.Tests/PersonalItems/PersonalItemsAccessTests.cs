using System.Text.Json.Nodes;
using Atlas.Server.Features.Access.Commands;
using Atlas.Server.Features.Access.Contracts;
using Atlas.Server.Features.Access.Queries;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Objects.Queries;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PersonalItems;
using Atlas.Server.Features.PersonalItems.Commands;
using Atlas.Server.Features.PersonalItems.Contracts;
using Atlas.Server.Features.PersonalItems.Queries;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PersonalItems;

/// <summary>
/// The rights matrix of personal items (spec 2026-10-29 §2): a note is its owner's; a grant opens one note to a
/// person or group for reading or editing within its validity; an expired grant, anyone else and a WikiAdmin get 404. Only the owner
/// shares and trashes. The lists Můj prostor and Sdíleno se mnou follow the same rule.
/// </summary>
public sealed class PersonalItemsAccessTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task A_note_opens_to_its_owner_and_grantees_only_never_to_an_administrator()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var (owner, reader, editor, lapsed, member) = (f.Writer, f.Reader, f.Outsider, await f.Host.SeedUserAsync("lapsed"), await f.Host.SeedUserAsync("member"));
        var group = await f.Host.SeedGroupAsync(TestLogin.For("Tým"), member);
        var note = await f.As<ObjectDto>(owner, new CreateObject(NoteObjectType.Id, ObjectScopes.Private, null, new JsonObject { ["title"] = "**Plán** dovolené", ["text"] = "- moře" }));
        Assert.Equal(("Plán dovolené", owner.Id, true), (note.Title, note.OwnerId, note.CanEdit));

        await f.As(owner, new SetItemGrant(note.Id, reader.Id, null, 1, null, null, null));
        await f.As(owner, new SetItemGrant(note.Id, editor.Id, null, 2, null, DateTimeOffset.UtcNow.AddDays(7), null));
        await f.As(owner, new SetItemGrant(note.Id, null, group, 1, null, null, null));
        // Grants are immutable rows: an expired one is written as it would stand once its end passed.
        await f.Host.SeedAsync(session => session.CreateSQLQuery("""
            INSERT INTO "ResourceGrant"("Id","ObjectId","UserId","Role","StartAt","ExpiresAt","ActiveFrom")
            VALUES (gen_random_uuid(), :item, :user, 2, now() - interval '2 days', now() - interval '1 day', now() - interval '2 days')
            """).SetParameter("item", note.Id).SetParameter("user", lapsed.Id).ExecuteUpdateAsync());

        async Task<string> NoteAccessAsync(UserAccount actor)
        {
            try { return (await f.As<ObjectDto>(actor, new GetObject(note.Id))).CanEdit ? "write" : "read"; }
            catch (ApiException ex) when (ex.Status == 404) { return "none"; }
        }
        var people = new[] { ("owner", owner), ("reader", reader), ("editor", editor), ("lapsed", lapsed), ("member", member), ("admin", f.Admin) };
        Assert.Equal("owner:write reader:read editor:write lapsed:none member:read admin:none",
            string.Join(' ', await Task.WhenAll(people.Select(async p => $"{p.Item1}:{await NoteAccessAsync(p.Item2)}"))));

        // Reading is not editing; editing is not sharing or trashing.
        await BoardsFixture.Refused(403, () => f.As(reader, new ApplyObjectChange(note.Id, Guid.NewGuid(), null, Fields("text", "x"))));
        Assert.Equal(2L, (await f.As<ObjectChangeResult>(editor, new ApplyObjectChange(note.Id, Guid.NewGuid(), null, Fields("text", "- hory")))).Version);
        await BoardsFixture.Refused(403, () => f.As(editor, new TrashObject(note.Id)));
        await BoardsFixture.Refused(403, () => f.As(editor, new SetItemGrant(note.Id, f.Admin.Id, null, 1, null, null, null)));
        await BoardsFixture.Refused(404, () => f.As(f.Admin, new SetItemGrant(note.Id, f.Admin.Id, null, 1, null, null, null)));
        await BoardsFixture.Refused(404, () => f.As(f.Admin, new GetResourceAccess("item", note.Id.ToString(), null)));
        // A board is no personal item (personal boards were dropped, owner 29. 10.): a space board is not shared one by one.
        var board = await f.CreateBoardAsync(title: "Prostor");
        await BoardsFixture.Refused(404, () => f.As(f.Writer, new SetItemGrant(board, reader.Id, null, 1, null, null, null)));

        // Můj prostor and Sdíleno se mnou.
        var mine = await f.As<IReadOnlyList<PersonalItemDto>>(owner, new ListPersonalItems());
        Assert.Equal(("Plán dovolené", 3, "hory"), mine.Select(x => (x.Title, x.Shares, x.Excerpt)).Single());
        var shared = Assert.Single(await f.As<IReadOnlyList<PersonalItemDto>>(editor, new ListSharedWithMe()));
        Assert.Equal(("writer", "write", true, "hory"), (shared.OwnerName, shared.Level, shared.ExpiresAt is not null, shared.Excerpt));
        Assert.Equal("read", Assert.Single(await f.As<IReadOnlyList<PersonalItemDto>>(member, new ListSharedWithMe())).Level);
        Assert.Empty(await f.As<IReadOnlyList<PersonalItemDto>>(lapsed, new ListSharedWithMe()));
        Assert.Empty(await f.As<IReadOnlyList<PersonalItemDto>>(f.Admin, new ListSharedWithMe()));
        Assert.Empty(await f.As<IReadOnlyList<PersonalItemDto>>(f.Admin, new ListPersonalItems()));
        Assert.Empty(await f.As<IReadOnlyList<PersonalItemDto>>(owner, new ListSharedWithMe()));

        // Ending a share closes the item again; the owner trashes it for everyone.
        var version = (await f.As<ResourceAccess>(owner, new GetResourceAccess("item", note.Id.ToString(), null))).Entries.Single(x => x.Subject.Id == reader.Id).Sources.Single().Version;
        await f.As(owner, new SetItemGrant(note.Id, reader.Id, null, null, null, null, version));
        Assert.Equal("none", await NoteAccessAsync(reader));
        await f.As(owner, new TrashObject(note.Id));
        Assert.Equal("none", await NoteAccessAsync(editor));
        Assert.Empty(await f.As<IReadOnlyList<PersonalItemDto>>(editor, new ListSharedWithMe()));
    }

    [PostgreSqlFact]
    public async Task The_owner_shares_through_the_item_access_kind_with_versions_and_validity_and_sees_who_has_access()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var note = await f.As<ObjectDto>(f.Writer, new CreateObject(NoteObjectType.Id, ObjectScopes.Private, null, new JsonObject { ["title"] = "Porada" }));
        var until = DateTimeOffset.UtcNow.AddDays(3);
        await f.As(f.Writer, new SetAccessGrant("item", note.Id, new UpdateAccessGrant("user", f.Reader.Id, "read", null, until, null)));

        var access = await f.As<ResourceAccess>(f.Writer, new GetResourceAccess("item", note.Id.ToString(), "direct"));
        var entry = Assert.Single(access.Entries);
        Assert.Equal(("Porada", "Poznámka", "reader", "read"), (access.Resource.Title, access.Resource.ParentTitle, entry.Subject.Name, entry.Level));
        Assert.Equal("Vlastník", Assert.Single(access.Automatic).Label);
        var version = entry.Sources.Single().Version;

        // A change carries the current version; a stale one is refused, a write level opens editing.
        await BoardsFixture.Refused(409, () => f.As(f.Writer, new SetAccessGrant("item", note.Id, new UpdateAccessGrant("user", f.Reader.Id, "write", null, null, null))));
        await f.As(f.Writer, new SetAccessGrant("item", note.Id, new UpdateAccessGrant("user", f.Reader.Id, "write", null, null, version)));
        Assert.True((await f.As<ObjectDto>(f.Reader, new GetObject(note.Id))).CanEdit);
        await BoardsFixture.Refused(400, () => f.As(f.Writer, new SetAccessGrant("item", note.Id, new UpdateAccessGrant("user", f.Reader.Id, "manage", null, null, null))));
        await BoardsFixture.Refused(400, () => f.As(f.Writer, new SetAccessGrant("item", note.Id, new UpdateAccessGrant("user", f.Writer.Id, "read", null, null, null))));
        // The share's search leaves the owner out (UX1-3); the server refuses a self-share all the same (above). Searched by the owner's
        // own login: the wiki holds every test's "writer".
        Assert.Contains((await f.As<IReadOnlyList<AccessCandidate>>(f.Writer, new GetAccessCandidates("user", f.Writer.Username))), x => x.Id == f.Writer.Id);
        Assert.Empty(await f.As<IReadOnlyList<AccessCandidate>>(f.Writer, new GetAccessCandidates("user", f.Writer.Username, true)));

        // The grantee's own card lists the item with its owner; an administrator's card of the grantee does not.
        var card = await f.As<PrincipalAccess>(f.Reader, new GetMyAccess());
        var item = Assert.Single(card.Entries, x => x.Kind == "item");
        Assert.Equal(("Porada", "write", "Sdílí writer"), (item.Title, item.Level, item.Note));
        Assert.DoesNotContain((await f.As<PrincipalAccess>(f.Admin, new GetPrincipalAccess("user", f.Reader.Id, null))).Entries, x => x.Kind == "item");

        var current = (await f.As<ResourceAccess>(f.Writer, new GetResourceAccess("item", note.Id.ToString(), null))).Entries.Single().Sources.Single().Version;
        await f.As(f.Writer, new RevokeAccessGrant("item", note.Id, "user", f.Reader.Id, current));
        await BoardsFixture.Refused(404, () => f.As(f.Reader, new GetObject(note.Id)));
        Assert.Empty((await f.As<ResourceAccess>(f.Writer, new GetResourceAccess("item", note.Id.ToString(), null))).Entries);
    }

    [PostgreSqlFact]
    public async Task A_note_is_always_private_and_validates_its_title_and_text()
    {
        await using var f = await BoardsFixture.SharedAsync();
        await BoardsFixture.Refused(400, () => f.As(f.Writer, new CreateObject(NoteObjectType.Id, ObjectScopes.Space, f.SpaceId, new JsonObject { ["title"] = "X" })), "invalid_object_change");
        await BoardsFixture.Refused(400, () => f.As(f.Writer, new CreateObject(NoteObjectType.Id, ObjectScopes.Private, null, new JsonObject { ["text"] = "bez názvu" })), "invalid_object_change");
        await BoardsFixture.Refused(400, () => f.As(f.Writer, new CreateObject(NoteObjectType.Id, ObjectScopes.Private, null, new JsonObject { ["title"] = "a\nb" })), "invalid_object_change");
        var note = await f.As<ObjectDto>(f.Writer, new CreateObject(NoteObjectType.Id, ObjectScopes.Private, null, new JsonObject { ["title"] = "Nákup" }));
        // The list carries a plain-text excerpt of a note, never its whole text (PERF1-2): the full text is the object's read.
        var longText = "**Seznam** " + string.Concat(Enumerable.Repeat("mleko a chleb ", 2000));
        await f.As(f.Writer, new CreateObject(NoteObjectType.Id, ObjectScopes.Private, null, new JsonObject { ["title"] = "Dlouhá", ["text"] = longText }));
        var listed = await f.As<IReadOnlyList<PersonalItemDto>>(f.Writer, new ListPersonalItems());
        Assert.Equal("", listed.Single(x => x.Id == note.Id).Excerpt);
        var excerpt = listed.Single(x => x.Title == "Dlouhá").Excerpt!;
        Assert.StartsWith("Seznam mleko a chleb", excerpt);
        Assert.True(excerpt.Length <= PersonalItemsQueryHandler.ExcerptLength + 1 && excerpt.EndsWith('…'), excerpt);
        await BoardsFixture.Refused(400, () => f.As(f.Writer, new ApplyObjectChange(note.Id, Guid.NewGuid(), null, Fields("color", "red"))), "invalid_object_change");
    }

    /// <summary>PER2-2: the envelope's backstop counts JSON as stored, so Czech text and line breaks reach the note's own limit, and beyond it
    /// the note refuses with its own message, not the envelope's.</summary>
    [PostgreSqlFact]
    public async Task A_czech_note_reaches_its_text_limit_and_beyond_it_gets_the_notes_message()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var czech = string.Concat(Enumerable.Repeat("ř", 12_000));
        var note = await f.As<ObjectDto>(f.Writer, new CreateObject(NoteObjectType.Id, ObjectScopes.Private, null, new JsonObject { ["title"] = "Řeč", ["text"] = czech }));
        var full = string.Concat(Enumerable.Repeat("žluťoučký\n", NoteObjectType.MaxText / 10 - 1)) + "kůň úpěl.";
        Assert.Equal(NoteObjectType.MaxText - 1, full.Length);
        Assert.Equal(2L, (await f.As<ObjectChangeResult>(f.Writer, new ApplyObjectChange(note.Id, Guid.NewGuid(), null, Fields("text", full + "!")))).Version);
        Assert.Equal(3L, (await f.As<ObjectChangeResult>(f.Writer, new ApplyObjectChange(note.Id, Guid.NewGuid(), null, Fields("title", "Řeč celá")))).Version);
        Assert.Equal(full + "!", (await f.As<ObjectDto>(f.Writer, new GetObject(note.Id))).Data!["text"]!.GetValue<string>());

        var over = await BoardsFixture.Refused(400, () => f.As(f.Writer, new ApplyObjectChange(note.Id, Guid.NewGuid(), null, Fields("text", full + "!!"))), "invalid_object_change");
        Assert.Equal($"Text poznámky smí mít nejvýše {NoteObjectType.MaxText} znaků a žádné řídicí znaky.", over.Message);
    }

    /// <summary>The trash of Můj prostor is the object trash (spec 2026-10-29 §5, ARCH1-2): only the owner lists, restores and purges a
    /// note; a trashed note leaves Sdíleno se mnou, a restore keeps its shares, a purge closes them (access history stays) and leaves one
    /// audit row the Security centre lists without the note's title (SEC1-4).</summary>
    /// <summary>PERF3-1: a trashed note's row carries its data without the text and the plain excerpt of Můj prostor instead, so 50
    /// long notes are no 5 MB page; the same shape goes to /api/v1 and MCP.</summary>
    [PostgreSqlFact]
    public async Task A_trashed_note_is_listed_with_an_excerpt_not_its_whole_text()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var text = string.Concat(Enumerable.Repeat("**Tajné** heslo k VPN. ", 3000));
        var note = (await f.As<ObjectDto>(f.Writer, new CreateObject(NoteObjectType.Id, ObjectScopes.Private, null,
            new JsonObject { ["title"] = "Dlouhá", ["text"] = text }))).Id;
        await f.As(f.Writer, new TrashObject(note));

        var row = Assert.Single((await f.As<ObjectTrashList>(f.Writer, new ListObjectTrash(null, ObjectScopes.Private, null))).Items);
        Assert.Equal((note, "Dlouhá"), (row.Id, row.Title));
        Assert.False(row.Data!.ContainsKey("text"));
        Assert.StartsWith("Tajné heslo k VPN. Tajné", row.Excerpt);
        Assert.InRange(row.Excerpt!.Length, 150, PersonalItemsQueryHandler.ExcerptLength + 1);
    }

    [PostgreSqlFact]
    public async Task Only_the_owner_sees_restores_and_purges_a_trashed_note_and_a_restore_keeps_its_shares()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var (owner, reader, editor) = (f.Writer, f.Reader, f.Outsider);
        var note = (await f.As<ObjectDto>(owner, new CreateObject(NoteObjectType.Id, ObjectScopes.Private, null, new JsonObject { ["title"] = "Poznámka" }))).Id;
        await f.As(owner, new SetItemGrant(note, reader.Id, null, 1, null, null, null));
        await f.As(owner, new SetItemGrant(note, editor.Id, null, 2, null, null, null));
        await f.As(owner, new TrashObject(note));

        Assert.Empty(await f.As<IReadOnlyList<PersonalItemDto>>(reader, new ListSharedWithMe()));
        await BoardsFixture.Refused(404, () => f.As(editor, new GetObject(note)));
        Assert.Empty(await f.As<IReadOnlyList<PersonalItemDto>>(owner, new ListPersonalItems()));
        Assert.Equal(new[] { note }, (await f.As<ObjectTrashList>(owner, new ListObjectTrash(null, ObjectScopes.Private, null))).Items.Select(x => x.Id));
        foreach (var other in new[] { reader, editor, f.Admin })
        {
            Assert.DoesNotContain((await f.As<ObjectTrashList>(other, new ListObjectTrash(null, null, null))).Items, x => x.Id == note);
            await Missing(() => f.As(other, new RestoreObject(note)));
            await Missing(() => f.As(other, new PurgeObject(note)));
        }

        await f.As<ObjectChangeResult>(owner, new RestoreObject(note));
        var shared = await f.As<IReadOnlyList<PersonalItemDto>>(editor, new ListSharedWithMe());
        Assert.Equal(new[] { (note, "write") }, shared.Select(x => (x.Id, x.Level!)));

        await f.As(owner, new TrashObject(note));
        await f.As(owner, new PurgeObject(note));
        using var session = f.Host.OpenSession();
        async Task<long> CountAsync(string sql) => await session.CreateSQLQuery(sql).SetParameter("note", note).UniqueResultAsync<long>();
        Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM "ContentResource" WHERE "Id" = :note"""));
        Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM "ResourceGrant" WHERE "ObjectId" = :note AND "ActiveTo" IS NULL"""));
        Assert.Equal(2L, await CountAsync("""SELECT count(*) FROM "ResourceGrant" WHERE "ObjectId" = :note"""));
        Assert.Equal(1L, await CountAsync("""SELECT count(*) FROM "ObjectPurgeEvent" WHERE "ObjectId" = :note"""));
        Assert.Equal(1L, await CountAsync("""
            SELECT count(*) FROM "AccessAuditView" v JOIN "ObjectPurgeEvent" e ON e."Id" = v."ObjectPurgeEventId"
            WHERE e."ObjectId" = :note AND v."Action" = 'object.purged' AND v."ObjectTypeId" = 'atlas.note' AND v."ObjectTitle" IS NULL
            """));
        Assert.Empty((await f.As<ObjectTrashList>(owner, new ListObjectTrash(null, ObjectScopes.Private, null))).Items);
    }

    private static async Task Missing(Func<Task> action) => Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(action)).Status);

    private static JsonObject Fields(string key, string value) => new() { ["fields"] = new JsonObject { [key] = value } };
}
