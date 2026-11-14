using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NHibernate;
using NHibernate.SqlCommand;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Collaboration.Queries;
using Atlas.Server.Features.ContentExport.Contracts;
using Atlas.Server.Features.ContentExport.Queries;
using Atlas.Server.Features.PageSource.Commands;
using Atlas.Server.Features.PageSource.Contracts;
using Atlas.Server.Features.PageHistory.Queries;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Queries;
using Atlas.Server.Features.Vaults.References;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Vaults;

/// <summary>
/// Password references against real vault rights (WP4 V6): the collaboration guard (author and every peer must Discover, inline
/// values always refused, removed secrets, rewrites of existing nodes), the join and refresh check, the batch evaluator, and the
/// reader projections (page, history) with read-only copies for editors who may not Discover a reference.
/// </summary>
public sealed class SecretReferenceGuardTests
{
    private const string Fixture = "argon2d-aes-gzip.kdbx";

    [PostgreSqlFact]
    public async Task Guard_admits_a_reference_only_when_the_author_and_every_peer_may_discover_it()
    {
        await using var f = await VaultFixture.CreateAsync();
        var s = await SetUpAsync(f);
        var guard = f.Host.Services.GetRequiredService<IReservedNodeGuard>();

        Assert.True(await guard.AdmitAsync(Request(s.DocumentId, s.Writer, [s.Writer, s.Peer], New(s.Router)), default));
        Assert.Equal(1L, await CountAsync(f, s.PageId));
        // The author, or any current peer, without Discover on a secret the document does not hold yet.
        Assert.False(await guard.AdmitAsync(Request(s.DocumentId, s.Stranger, [s.Stranger], New(s.Mail)), default));
        Assert.False(await guard.AdmitAsync(Request(s.DocumentId, s.Writer, [s.Writer, s.Peer, s.Stranger], New(s.Mail)), default));
        // An inline value is refused whoever writes it, under any attribute name.
        Assert.False(await guard.AdmitAsync(Request(s.DocumentId, s.Writer, [s.Writer], New(s.Router, ("value", "\"CANARY\""))), default));
        Assert.False(await guard.AdmitAsync(Request(s.DocumentId, s.Writer, [s.Writer], New(s.Router, ("password", "\"CANARY\""))), default));
        Assert.False(await guard.AdmitAsync(Request(s.DocumentId, s.Writer, [s.Writer], new ReservedNodeChange("secretReference", false, Attributes(("value", "\"CANARY\"")))), default));
        // A new node must name a secret; a rewrite of an existing node may change its presentation only, or its id to one all may use.
        Assert.False(await guard.AdmitAsync(Request(s.DocumentId, s.Writer, [s.Writer], new ReservedNodeChange("secretReference", true, Attributes(("field", "\"url\"")))), default));
        Assert.True(await guard.AdmitAsync(Request(s.DocumentId, s.Stranger, [s.Stranger], new ReservedNodeChange("secretInlineReference", false, Attributes(("showCopy", "false")))), default));
        Assert.True(await guard.AdmitAsync(Request(s.DocumentId, s.Writer, [s.Writer, s.Peer], new ReservedNodeChange("secretInlineReference", false, Attributes(("secretId", Json(s.Mail))))), default));
        Assert.Equal(2L, await CountAsync(f, s.PageId));
        // A removed secret is Discover for nobody, WikiAdmin included: it cannot enter a document. One the document already holds
        // stays admissible, since every snapshot repeats it.
        await f.Host.ExecuteAsync("UPDATE \"VaultSecret\" SET \"RemovedAt\" = now() WHERE \"Id\" = :id", s.Other);
        Assert.False(await guard.AdmitAsync(Request(s.DocumentId, f.Admin, [f.Admin], New(s.Other)), default));
        await f.Host.ExecuteAsync("UPDATE \"VaultSecret\" SET \"RemovedAt\" = now() WHERE \"Id\" = :id", s.Mail);
        Assert.True(await guard.AdmitAsync(Request(s.DocumentId, s.Writer, [s.Writer, s.Peer], New(s.Mail)), default));
        // Unknown secrets, documents and accounts fail closed.
        Assert.False(await guard.AdmitAsync(Request(s.DocumentId, f.Admin, [f.Admin], New(Guid.NewGuid())), default));
        Assert.False(await guard.AdmitAsync(Request("page:x", s.Writer, [s.Writer], New(s.Router)), default));
        Assert.False(await guard.AdmitAsync(Request(s.DocumentId, new UserAccount { Id = Guid.NewGuid() }, [], New(s.Router)), default));
        Assert.False(await guard.AdmitAsync(new ReservedNodeRequest(s.DocumentId, s.Writer.Id.ToString(), [s.Writer.Id.ToString()],
            [new ReservedNodeChange("youtrackIssue", true, Attributes(("secretId", Json(s.Router))))]), default));
    }

    [PostgreSqlFact]
    public async Task Joining_needs_discover_on_every_reference_and_a_revocation_denies_at_the_next_refresh()
    {
        await using var f = await VaultFixture.CreateAsync();
        var s = await SetUpAsync(f);
        var guard = f.Host.Services.GetRequiredService<IReservedNodeGuard>();
        Assert.True(await CanJoinAsync(f, s, s.Stranger));
        Assert.True(await guard.AdmitAsync(Request(s.DocumentId, s.Writer, [s.Writer], New(s.Router)), default));

        Assert.True(await CanJoinAsync(f, s, s.Writer));
        Assert.True(await CanJoinAsync(f, s, s.Peer));
        Assert.False(await CanJoinAsync(f, s, s.Stranger));
        f.Host.Actor.Current = s.Stranger;
        var refused = await Assert.ThrowsAsync<ApiException>(() => f.Host.InvokeAsync<WikiCollaborationSession>(new OpenCollaboration(s.PageId)));
        Assert.Equal((403, "hidden_secret_references"), (refused.Status, refused.Code));
        Assert.DoesNotContain(s.Router.ToString(), refused.Message);

        // V3 grant revocation calls AccessChanges; the refresh that follows authorizes every peer again, and this one is out.
        var version = (await f.As<VaultAccessState>(f.SpaceAdmin, new GetVaultAccess(s.VaultId))).People.Single(p => p.UserId == s.Peer.Id).Sources.Single().Version;
        await f.As(f.SpaceAdmin, new RevokeVaultGrant(s.VaultId, s.Peer.Id, null, version));
        Assert.False(await CanJoinAsync(f, s, s.Peer));

        // A reference whose secret is gone hides nothing, so it keeps nobody out.
        await f.Host.ExecuteAsync("UPDATE \"VaultSecret\" SET \"RemovedAt\" = now() WHERE \"Id\" = :id", s.Router);
        Assert.True(await CanJoinAsync(f, s, s.Stranger));
        await f.Host.ExecuteAsync("UPDATE \"VaultSecret\" SET \"RemovedAt\" = NULL WHERE \"Id\" = :id", s.Router);

        // A reset (a save outside the live session) starts over from the saved revision: the admitted reference no longer counts.
        await f.Host.Pages().SaveAsync(s.PageId, new("Stránka", Rich(), "", (await f.Host.Pages().GetAsync(s.PageId, f.SpaceAdmin, default)).Version), f.SpaceAdmin, default);
        f.Host.Actor.Current = s.Stranger;
        var reopened = await f.Host.InvokeAsync<WikiCollaborationSession>(new OpenCollaboration(s.PageId));
        Assert.True(await CanJoinAsync(f, s with { Generation = reopened.Generation }, s.Stranger));
    }

    [PostgreSqlFact]
    public async Task Readers_and_editors_without_discover_get_a_read_only_copy_without_the_reference()
    {
        await using var f = await VaultFixture.CreateAsync();
        var s = await SetUpAsync(f);
        var pages = f.Host.Pages();
        var current = await pages.GetAsync(s.PageId, f.SpaceAdmin, default);
        var content = Rich(Reference(s.Router));
        // The author must Discover what it references; the space Admin manages the vault.
        var unusable = await Assert.ThrowsAsync<ApiException>(() => pages.SaveAsync(s.PageId, new("Stránka", Rich(Reference(s.Router)), "", current.Version), s.Stranger, default));
        Assert.Equal((400, "secret_reference_unavailable"), (unusable.Status, unusable.Code));
        var saved = await pages.SaveAsync(s.PageId, new("Stránka", content, "", current.Version), f.SpaceAdmin, default);
        Assert.Contains(s.Router.ToString(), saved.ContentJson);

        var writer = await pages.GetAsync(s.PageId, s.Writer, default);
        Assert.Contains(s.Router.ToString(), writer.ContentJson);
        Assert.True(writer.CanEdit);
        var stranger = await pages.GetAsync(s.PageId, s.Stranger, default);
        Assert.DoesNotContain(s.Router.ToString(), stranger.ContentJson);
        Assert.DoesNotContain("secretReference", stranger.ContentJson);
        Assert.False(stranger.CanEdit);
        // Saving the stripped copy would drop the reference: refused, and the reference stays.
        var hidden = await Assert.ThrowsAsync<ApiException>(() => pages.SaveAsync(s.PageId, new("Stránka", stranger.ContentJson, "", stranger.Version), s.Stranger, default));
        Assert.Equal((403, "hidden_secret_references"), (hidden.Status, hidden.Code));
        Assert.Contains(s.Router.ToString(), (await pages.GetAsync(s.PageId, f.SpaceAdmin, default)).ContentJson);

        // History serves the same projection.
        f.Host.Actor.Current = s.Stranger;
        var revision = await f.Host.InvokeAsync<PageHistoryRevision>(new GetPageRevision(s.PageId, (await RevisionIdAsync(f, s.PageId))));
        Assert.DoesNotContain(s.Router.ToString(), revision.ContentJson);
        f.Host.Actor.Current = s.Writer;
        Assert.Contains(s.Router.ToString(), (await f.Host.InvokeAsync<PageHistoryRevision>(new GetPageRevision(s.PageId, await RevisionIdAsync(f, s.PageId)))).ContentJson);

        // Once the KeePass entry is gone, the dead reference is shown to nobody and no longer makes the page read-only.
        await f.Host.ExecuteAsync("UPDATE \"VaultSecret\" SET \"RemovedAt\" = now() WHERE \"Id\" = :id", s.Router);
        var dead = await pages.GetAsync(s.PageId, s.Stranger, default);
        Assert.DoesNotContain(s.Router.ToString(), dead.ContentJson);
        Assert.True(dead.CanEdit);
        Assert.DoesNotContain(s.Router.ToString(), (await pages.GetAsync(s.PageId, f.Admin, default)).ContentJson);
        await pages.SaveAsync(s.PageId, new("Stránka", dead.ContentJson, "", dead.Version), s.Stranger, default);
        // Keeping a dead reference while editing other content is fine; adding one is not.
        var kept = await pages.SaveAsync(s.PageId, new("Stránka", Rich(Reference(s.Mail)), "", (await pages.GetAsync(s.PageId, f.SpaceAdmin, default)).Version), f.SpaceAdmin, default);
        await f.Host.ExecuteAsync("UPDATE \"VaultSecret\" SET \"RemovedAt\" = now() WHERE \"Id\" = :id", s.Mail);
        await pages.SaveAsync(s.PageId, new("Jiný název", Rich(Reference(s.Mail)), "", kept.Version), s.Writer, default);
        var readded = await Assert.ThrowsAsync<ApiException>(() => pages.SaveAsync(s.PageId,
            new("Jiný název", Rich(Reference(s.Mail), Reference(s.Router)), "", kept.Version + 1), s.Writer, default));
        Assert.Equal("secret_reference_unavailable", readded.Code);
    }

    [PostgreSqlFact]
    public async Task Guard_checks_the_old_and_the_new_id_of_a_rewrite_and_trusts_admitted_snapshot_nodes()
    {
        await using var f = await VaultFixture.CreateAsync();
        var s = await SetUpAsync(f);
        var guard = f.Host.Services.GetRequiredService<IReservedNodeGuard>();
        Assert.True(await guard.AdmitAsync(Request(s.DocumentId, s.Writer, [s.Writer, s.Peer], New(s.Router)), default));
        // The stranger may Discover Mail only.
        await f.As(f.SpaceAdmin, new SetSecretGrant(s.Mail, new UpdateVaultGrant(s.Stranger.Id, null, "discover", null)));

        // A snapshot re-sends every admitted node as new: one with admitted attributes and the same id needs no decision, a
        // genuinely new one is decided like an update.
        Assert.True(await guard.AdmitAsync(Snapshot(Request(s.DocumentId, s.Stranger, [s.Stranger], Resent(s.Router))), default));
        Assert.False(await guard.AdmitAsync(Snapshot(Request(s.DocumentId, s.Stranger, [s.Stranger], New(s.Other))), default));
        // A presentation rewrite of an admitted node decides nothing.
        Assert.True(await guard.AdmitAsync(Request(s.DocumentId, s.Stranger, [s.Stranger], Rewrite(s.Router, null, ("showCopy", "false"))), default));
        // A rewrite to another id (the page source): the new id must be one all may Discover, and the author must not replace a
        // reference hidden from it, although it may use the new one.
        Assert.False(await guard.AdmitAsync(Request(s.DocumentId, s.Stranger, [s.Stranger], Rewrite(s.Router, s.Mail)), default));
        Assert.Equal(1L, await CountAsync(f, s.PageId));
        Assert.False(await guard.AdmitAsync(Request(s.DocumentId, s.Writer, [s.Writer, s.Stranger], Rewrite(s.Router, s.Other)), default));
        Assert.True(await guard.AdmitAsync(Request(s.DocumentId, s.Writer, [s.Writer, s.Peer], Rewrite(s.Router, s.Mail)), default));
        Assert.Equal(2L, await CountAsync(f, s.PageId));
        // A dead old reference hides nothing.
        await f.Host.ExecuteAsync("UPDATE \"VaultSecret\" SET \"RemovedAt\" = now() WHERE \"Id\" = :id", s.Router);
        Assert.True(await guard.AdmitAsync(Request(s.DocumentId, s.Stranger, [s.Stranger], Rewrite(s.Router, s.Mail)), default));
    }

    [PostgreSqlFact]
    public async Task Page_source_markdown_follows_the_save_rules_and_every_reader_copy_drops_what_it_may_not_discover()
    {
        await using var f = await VaultFixture.CreateAsync();
        var s = await SetUpAsync(f);
        // An admin of another space manages its pages, but none of the fixture space's vaults.
        var otherAdmin = await f.Host.SeedUserAsync("other-admin");
        var otherSpace = await f.Host.SeedSpaceAsync("Jiný prostor", (otherAdmin, SpaceRole.Admin));
        var pages = f.Host.Pages();
        // The server writes the stored Markdown, directives included, from the JSON.
        var directive = $"::secret{{secretId={s.Router}}}";
        var created = await pages.CreateAsync(new(otherSpace, null, "Síť", Rich(
            """{"type":"heading","attrs":{"level":1},"content":[{"type":"text","text":"Síť"}]}""", Reference(s.Router),
            """{"type":"paragraph","content":[{"type":"text","text":"Konec sítě"}]}"""), null), f.Admin, default);
        var page = await pages.PublishAsync(created.Id, new(created.Version), f.Admin, default);

        // Reader copies: the API, history and export keep a directive only for readers who may Discover its secret.
        Assert.Contains(directive, (await pages.GetAsync(page.Id, f.Admin, default)).Markdown);
        var copy = await pages.GetAsync(page.Id, otherAdmin, default);
        Assert.Equal("# Síť\n\n\n\nKonec sítě", copy.Markdown);
        Assert.False(copy.CanEdit);
        f.Host.Actor.Current = otherAdmin;
        var revision = await f.Host.InvokeAsync<PageHistoryRevision>(new GetPageRevision(page.Id, await RevisionIdAsync(f, page.Id)));
        Assert.DoesNotContain(s.Router.ToString(), revision.Markdown);
        await using (var archive = await f.As<ContentExportArchive>(otherAdmin, new BuildContentExport(page.Id, false)))
        {
            using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read, true);
            using var reader = new StreamReader(zip.GetEntry($"pages/{page.Id:D}/content.md")!.Open());
            var markdown = await reader.ReadToEndAsync();
            Assert.Contains("Konec sítě", markdown);
            Assert.DoesNotContain(s.Router.ToString(), markdown);
        }
        // The search read models are shared by every reader: never a directive, in the body, the normalized text or headings.
        var fragment = s.Router.ToString()[..8];
        Assert.Equal(0L, await ScalarAsync(f, $"""
            SELECT count(*) FROM "SearchDocument" WHERE "PageId" = :page
              AND ("Body" LIKE '%secret%' OR "NormalizedBody" LIKE '%secretid%' OR "NormalizedBody" LIKE '%{fragment}%')
            """, page.Id));
        Assert.Equal(1L, await ScalarAsync(f, """SELECT count(*) FROM "SearchDocument" WHERE "PageId" = :page AND "NormalizedBody" LIKE '%konec site%'""", page.Id));
        Assert.Equal(0L, await ScalarAsync(f, $"""
            SELECT count(*) FROM "WorkingSearchDocument" WHERE "PageId" = :page AND ("NormalizedBody" LIKE '%secretid%' OR "NormalizedBody" LIKE '%{fragment}%')
            """, page.Id));

        // The page source saves like a normal save. The other admin may not overwrite the reference hidden from it.
        var hidden = await Assert.ThrowsAsync<ApiException>(() => ReplaceSourceAsync(f, otherAdmin, page.Id, new(Rich(), "# Síť\n", page.Version)));
        Assert.Equal((403, "hidden_secret_references"), (hidden.Status, hidden.Code));
        // The author must Discover what it adds; an unknown secret is Discover for nobody.
        var unknown = Guid.NewGuid();
        var unusable = await Assert.ThrowsAsync<ApiException>(() => ReplaceSourceAsync(f, f.Admin, page.Id,
            new(Rich(Reference(s.Router), Reference(unknown)), $"{directive}\n\n::secret{{secretId={unknown}}}\n", page.Version)));
        Assert.Equal((400, "secret_reference_unavailable"), (unusable.Status, unusable.Code));
        // A directive the client writes (here with a value, which the JSON could never hold) is not what is stored.
        var replaced = await ReplaceSourceAsync(f, f.Admin, page.Id,
            new(Rich(Reference(s.Router), Reference(s.Mail)), $"{directive}\n\nVPN :secret{{secretId={s.Mail} value=CANARY}}\n", page.Version));
        Assert.Equal($"{directive}\n\n::secret{{secretId={s.Mail}}}", replaced.Markdown);
    }

    [PostgreSqlFact]
    public async Task Batch_decisions_take_the_same_queries_for_one_account_as_for_many()
    {
        await using var f = await VaultFixture.CreateAsync();
        var s = await SetUpAsync(f);
        var many = new List<UserAccount> { s.Writer, s.Peer, s.Stranger, f.Admin };
        for (var index = 0; index < 20; index++)
        {
            var user = await f.Host.SeedUserAsync($"peer-{index}");
            await f.As(f.SpaceAdmin, new SetVaultGrant(s.VaultId, new UpdateVaultGrant(user.Id, null, "discover", null)));
            many.Add(user);
        }
        var group = await f.Host.SeedGroupAsync("Provoz", many[^1]);
        await f.As(f.SpaceAdmin, new SetSecretGrant(s.Mail, new UpdateVaultGrant(null, group, "read", null)));

        async Task<(int Statements, IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> Decisions)> Decide(IReadOnlyCollection<UserAccount> users)
        {
            var counter = new StatementCounter();
            using var session = f.Host.OpenSession().SessionWithOptions().Interceptor(counter).OpenSession();
            var access = new SecretReferenceAccess(session, new VaultGrantSnapshots(session));
            var decisions = await access.DiscoverableAsync(users, [s.Router, s.Mail, Guid.NewGuid()], default);
            return (counter.Count, decisions);
        }
        var one = await Decide([s.Writer]);
        var all = await Decide(many);
        Assert.Equal(one.Statements, all.Statements);
        Assert.Equal(new[] { s.Router, s.Mail }.Order(), all.Decisions[s.Writer.Id].Order());
        Assert.Empty(all.Decisions[s.Stranger.Id]);
        Assert.Equal(2, all.Decisions[f.Admin.Id].Count);
        Assert.Equal(2, all.Decisions[many[^1].Id].Count);
    }

    internal sealed record Setup(Guid VaultId, Guid Router, Guid Mail, Guid Other, Guid PageId, Guid Generation, UserAccount Writer, UserAccount Peer,
        UserAccount Stranger)
    {
        public string DocumentId => $"page:{PageId:N}:{Generation:N}";
    }

    /// <summary>A vault of the fixture space, a page there, two writers with vault Discover and one writer without, and the
    /// page's live collaboration generation.</summary>
    internal static async Task<Setup> SetUpAsync(VaultFixture f)
    {
        var location = await f.LocalLocationAsync();
        var vault = await f.AttachAsync(location.Id, "provoz.kdbx", Fixture);
        var secrets = await f.As<IReadOnlyList<VaultSecretView>>(f.SpaceAdmin, new ListVaultSecrets(vault.Id));
        var writer = await f.Host.SeedUserAsync("writer");
        var peer = await f.Host.SeedUserAsync("peer");
        var stranger = await f.Host.SeedUserAsync("stranger");
        await f.Host.SeedAsync(async session =>
        {
            foreach (var user in new[] { writer, peer, stranger })
            {
                await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = user.Id, Role = (int)SpaceRole.Writer });
            }
        });
        await f.As(f.SpaceAdmin, new SetVaultGrant(vault.Id, new UpdateVaultGrant(writer.Id, null, "discover", null)));
        await f.As(f.SpaceAdmin, new SetVaultGrant(vault.Id, new UpdateVaultGrant(peer.Id, null, "read", null)));
        var page = await f.Host.Pages().CreateAsync(new(f.SpaceId, null, "Stránka", Rich(), ""), f.SpaceAdmin, default);
        // Published, so the page is no private draft of its author.
        await f.Host.Pages().PublishAsync(page.Id, new(page.Version), f.SpaceAdmin, default);
        f.Host.Actor.Current = writer;
        var session = await f.Host.InvokeAsync<WikiCollaborationSession>(new OpenCollaboration(page.Id));
        return new Setup(vault.Id, secrets.Single(x => x.Title == "Router").SecretId, secrets.Single(x => x.Title == "Mail").SecretId,
            secrets.First(x => x.Title is not "Router" and not "Mail").SecretId, page.Id, session.Generation, writer, peer, stranger);
    }

    internal static string Rich(params string[] blocks) =>
        SecretReferenceContentTests.Envelope("{\"type\":\"doc\",\"content\":[" + (blocks.Length == 0 ? "{\"type\":\"paragraph\"}" : string.Join(",", blocks)) + "]}",
            blocks.Length == 0 ? "[]" : "[{\"id\":\"secrets\",\"version\":4}]");

    internal static string Reference(Guid secretId) =>
        "{\"type\":\"secretReference\",\"attrs\":{\"secretId\":\"" + secretId + "\",\"field\":\"password\",\"fields\":null,\"showCopy\":true,\"showDetails\":false,\"autoHideSeconds\":60}}";

    private static async Task<bool> CanJoinAsync(VaultFixture f, Setup s, UserAccount user) =>
        (await f.Host.InvokeAsync<CollaboratorAuthorization>(new AuthorizeCollaborator(s.PageId, s.Generation, user.Id))).CanWrite;

    private static async Task<long> CountAsync(VaultFixture f, Guid pageId)
    {
        using var session = f.Host.OpenSession();
        return await session.CreateSQLQuery("SELECT count(*) FROM \"CollaborationSecretReference\" WHERE \"PageId\" = :page")
            .SetParameter("page", pageId).UniqueResultAsync<long>();
    }

    private static async Task<Guid> RevisionIdAsync(VaultFixture f, Guid pageId)
    {
        using var session = f.Host.OpenSession();
        return await session.QueryOver<PageRevision>().Where(r => r.PageId == pageId && r.ActiveTo == null).Select(r => r.Id).SingleOrDefaultAsync<Guid>();
    }

    private static Task<PageDto> ReplaceSourceAsync(VaultFixture f, UserAccount actor, Guid pageId, ReplacePageSourceRequest request) =>
        f.As<PageDto>(actor, new ReplacePageSource(pageId, request));

    private static async Task<long> ScalarAsync(VaultFixture f, string sql, Guid pageId)
    {
        using var session = f.Host.OpenSession();
        return Convert.ToInt64(await session.CreateSQLQuery(sql).SetParameter("page", pageId).UniqueResultAsync());
    }

    private static ReservedNodeRequest Snapshot(ReservedNodeRequest request) => request with { Payload = ReservedNodePayload.Snapshot };

    /// <summary>An admitted node as a snapshot sends it again: new, with its admitted attributes as the current ones.</summary>
    private static ReservedNodeChange Resent(Guid secretId) =>
        New(secretId) with { CurrentAttributes = Attributes(("secretId", Json(secretId)), ("field", "\"password\"")) };

    /// <summary>An attribute write on an admitted node of <paramref name="current"/>: the new id and any presentation attributes.</summary>
    private static ReservedNodeChange Rewrite(Guid current, Guid? next, params (string Key, string Json)[] extra) =>
        new("secretReference", false, Attributes([.. next is { } id ? new[] { ("secretId", Json(id)) } : [], .. extra]))
        {
            CurrentAttributes = Attributes(("secretId", Json(current)), ("field", "\"password\""))
        };

    private static ReservedNodeRequest Request(string documentId, UserAccount author, UserAccount[] peers, ReservedNodeChange change) =>
        new(documentId, author.Id.ToString(), peers.Select(p => p.Id.ToString()).ToArray(), [change]);

    private static ReservedNodeChange New(Guid secretId, params (string Key, string Json)[] extra) =>
        new("secretReference", true, Attributes([("secretId", Json(secretId)), ("field", "\"password\""), .. extra]));

    private static Dictionary<string, JsonElement> Attributes(params (string Key, string Json)[] values) =>
        values.ToDictionary(v => v.Key, v => JsonDocument.Parse(v.Json).RootElement.Clone());

    private static string Json(Guid id) => $"\"{id}\"";

    private sealed class StatementCounter : EmptyInterceptor
    {
        public int Count { get; private set; }

        public override SqlString OnPrepareStatement(SqlString sql)
        {
            Count++;
            return base.OnPrepareStatement(sql);
        }
    }
}

internal static class SecretReferenceTestHosts
{
    public static TestPages Pages(this AtlasTestHost host) => new(host);
}
