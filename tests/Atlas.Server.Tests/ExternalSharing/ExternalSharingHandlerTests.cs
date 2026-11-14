using System.Text.Json.Nodes;
using Atlas.Server.Features.ExternalSharing.Commands;
using Atlas.Server.Features.ExternalSharing.Contracts;
using Atlas.Server.Features.ExternalSharing.Domain;
using Atlas.Server.Features.ExternalSharing.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.ExternalSharing;

public sealed class ExternalSharingHandlerTests
{
    [PostgreSqlFact]
    public async Task Only_space_Admin_or_WikiAdmin_can_manage_policy_or_links()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        foreach (var (actor, status) in new[] { (fixture.Reader, 403), (fixture.Writer, 403), (fixture.Outsider, 404) })
        {
            fixture.Host.Actor.Current = actor;
            await Denied(status, () => fixture.Host.InvokeAsync<SharingPolicyDto>(new GetSharingPolicy(fixture.SpaceId)));
            await Denied(status, () => fixture.Host.InvokeAsync<SharingPolicyDto>(new SaveSharingPolicy(fixture.SpaceId, new(true, null))));
            await Denied(status, () => fixture.Host.InvokeAsync<ShareLinksDto>(new ListShareLinks(fixture.SpaceId, fixture.PageId)));
            await Denied(status, () => fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create()));
            await Denied(status, () => fixture.Host.InvokeAsync(new RevokeShareLink(fixture.PageId, Guid.NewGuid())));
        }
        fixture.Host.Actor.Current = fixture.Admin;
        var policy = await fixture.Host.InvokeAsync<SharingPolicyDto>(new SaveSharingPolicy(fixture.SpaceId, new(true, null)));
        var issue = await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create());
        Assert.Equal("active", issue.Link.Status);
        fixture.Host.Actor.Current = fixture.WikiAdmin;
        Assert.Equal(policy, await fixture.Host.InvokeAsync<SharingPolicyDto>(new GetSharingPolicy(fixture.SpaceId)));
        Assert.Equal("active", (await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create())).Link.Status);
        fixture.Host.Actor.Current = null;
        Assert.Equal("Published", (await fixture.Host.InvokeAsync<SharedPageDto>(new OpenSharedPage(ShareSecret.Hash(issue.Secret)))).Title);
    }

    [PostgreSqlFact]
    public async Task Concurrent_issuance_respects_twenty_live_links_and_lists_page_without_silent_truncation()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        await fixture.Enable();
        for (var i = 0; i < 19; i++) await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create("Link " + i));
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async i =>
        {
            try { return (Issued: await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create("Concurrent " + i)), Status: 201); }
            catch (ApiException exception) { return (Issued: (IssuedShareLink?)null, Status: exception.Status); }
        }));
        Assert.Single(results, result => result.Status == 201);
        Assert.Equal(2, results.Count(result => result.Status == 409));
        var first = await fixture.Host.InvokeAsync<ShareLinksDto>(new ListShareLinks(fixture.SpaceId, fixture.PageId, 0, 7));
        var next = await fixture.Host.InvokeAsync<ShareLinksDto>(new ListShareLinks(fixture.SpaceId, null, 7, 7));
        var last = await fixture.Host.InvokeAsync<ShareLinksDto>(new ListShareLinks(fixture.SpaceId, fixture.PageId, 14, 7));
        Assert.Equal(7, first.Items.Count);
        Assert.Equal(7, next.Items.Count);
        Assert.Equal(6, last.Items.Count);
        Assert.True(first.HasMore);
        Assert.True(next.HasMore);
        Assert.False(last.HasMore);
        Assert.Equal(20, first.Items.Concat(next.Items).Concat(last.Items).Select(link => link.Id).Distinct().Count());
        await fixture.Host.InvokeAsync(new RevokeShareLink(fixture.PageId, first.Items[0].Id));
        Assert.Equal("active", (await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create())).Link.Status);
        await Denied(400, () => fixture.Host.InvokeAsync<ShareLinksDto>(new ListShareLinks(fixture.SpaceId, null, -1, 7)));
        await Denied(400, () => fixture.Host.InvokeAsync<ShareLinksDto>(new ListShareLinks(fixture.SpaceId, null, 0, 101)));
    }

    [PostgreSqlFact]
    public async Task Concurrent_policy_changes_have_one_winner_and_same_version_noop_is_idempotent()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        fixture.Host.Actor.Current = fixture.Admin;
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { return (Policy: await fixture.Host.InvokeAsync<SharingPolicyDto>(new SaveSharingPolicy(fixture.SpaceId, new(true, null))), Status: 200); }
            catch (ApiException exception) { return (Policy: (SharingPolicyDto?)null, Status: exception.Status); }
        }));
        var winner = Assert.Single(results, result => result.Status == 200).Policy!;
        Assert.Single(results, result => result.Status == 409);
        Assert.Equal(winner, await fixture.Host.InvokeAsync<SharingPolicyDto>(new SaveSharingPolicy(fixture.SpaceId, new(true, winner.Version))));
        using var read = fixture.Host.OpenSession();
        Assert.Equal(1, await read.QueryOver<SpaceExternalSharingPolicy>().Where(x => x.SpaceId == fixture.SpaceId).RowCountAsync());
    }

    [PostgreSqlFact]
    public async Task Expired_capability_denies_content_and_attachment_and_other_page_cannot_revoke_it()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        await fixture.Enable();
        var issued = await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create());
        var other = await fixture.Host.InvokeAsync<PageDto>(new CreatePage(new(fixture.SpaceId, null, "Other", SharingSite.Rich("Other"), "Other")));
        await Denied(404, () => fixture.Host.InvokeAsync(new RevokeShareLink(other.Id, issued.Link.Id)));
        await fixture.Host.ExecuteAsync("UPDATE \"PageShareLink\" SET \"ActiveFrom\"=now()-interval '1 day',\"ExpiresAt\"=now() WHERE \"Id\"=:id", issued.Link.Id);
        var hash = ShareSecret.Hash(issued.Secret);
        await Denied(404, () => fixture.Host.InvokeAsync<SharedPageDto>(new OpenSharedPage(hash)));
        await Denied(404, () => fixture.Host.InvokeAsync<Atlas.Server.Features.Attachments.Contracts.AttachmentContentDto>(new OpenSharedAttachment(hash, Guid.NewGuid(), false)));
        var listed = await fixture.Host.InvokeAsync<ShareLinksDto>(new ListShareLinks(fixture.SpaceId, fixture.PageId));
        Assert.Equal("expired", Assert.Single(listed.Items).Status);
        using var read = fixture.Host.OpenSession();
        var persisted = await read.GetAsync<PageShareLink>(issued.Link.Id);
        Assert.Equal(64, persisted.TokenHash.Length);
        Assert.DoesNotContain(issued.Secret, persisted.TokenHash);
    }

    [PostgreSqlFact]
    public async Task Closing_only_direct_Admin_grant_then_regranting_does_not_revive_old_link()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        await fixture.Enable();
        var issued = await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create());
        await fixture.Host.CloseAccessAsync(AccessAuditLog.SpaceGrant, $"\"UserId\"=:id AND \"SpaceId\"='{fixture.SpaceId}'", fixture.Admin.Id);
        await fixture.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = fixture.SpaceId,
            UserId = fixture.Admin.Id, Role = 3, ActiveFrom = DateTime.UtcNow }));
        await Denied(404, () => fixture.Host.InvokeAsync<SharedPageDto>(new OpenSharedPage(ShareSecret.Hash(issued.Secret))));
        Assert.Equal("active", (await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create())).Link.Status);
    }

    [PostgreSqlFact]
    public async Task Page_links_show_only_titles_of_pages_every_space_reader_reads_and_never_their_ids()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        await fixture.Enable();
        fixture.Host.Actor.Current = fixture.Writer;
        var open = await fixture.Host.InvokeAsync<PageDto>(new CreatePage(new(fixture.SpaceId, null, "Provozní řád", SharingSite.Rich("x"), "x")));
        await fixture.Host.InvokeAsync<PageDto>(new PublishPage(open.Id, new(open.Version)));
        var draft = await fixture.Host.InvokeAsync<PageDto>(new CreatePage(new(fixture.SpaceId, null, "Propouštění Q4", SharingSite.Rich("x"), "x")));
        var board = Guid.NewGuid();
        static JsonObject Link(Guid id, string title, string route) => new()
        {
            ["type"] = "pageReference", ["attrs"] = new JsonObject { ["id"] = id.ToString(), ["title"] = title, ["url"] = $"#/{route}/{id}" }
        };
        var content = new JsonObject
        {
            ["format"] = "atlas-rich", ["version"] = 1,
            ["document"] = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(
                Link(open.Id, "Starý řád", "pages"), Link(draft.Id, "Propouštění Q4", "pages"), Link(board, "Osobní tabule", "boards")) }) },
            ["plugins"] = new JsonArray(new JsonObject { ["id"] = "pages", ["version"] = 1 })
        }.ToJsonString();
        var shared = await fixture.Host.InvokeAsync<PageDto>(new CreatePage(new(fixture.SpaceId, null, "Odkazy", content, "Odkazy")));
        await fixture.Host.InvokeAsync<PageDto>(new PublishPage(shared.Id, new(shared.Version)));
        fixture.Host.Actor.Current = fixture.Admin;
        var issued = await fixture.Host.InvokeAsync<IssuedShareLink>(new CreateShareLink(shared.Id, new("Review", DateTimeOffset.UtcNow.AddDays(1))));
        fixture.Host.Actor.Current = null;

        var page = await fixture.Host.InvokeAsync<SharedPageDto>(new OpenSharedPage(ShareSecret.Hash(issued.Secret)));

        var texts = JsonNode.Parse(page.ContentJson)!["document"]!["content"]![0]!["content"]!.AsArray().Select(node => node!["text"]!.GetValue<string>());
        Assert.Equal(["Provozní řád", "Odkaz na stránku", "Odkaz na stránku"], texts);
        foreach (var hidden in new[] { "Propouštění", "Osobní tabule", "Starý řád", open.Id.ToString(), draft.Id.ToString(), board.ToString(), "pageReference" })
            Assert.DoesNotContain(hidden, page.ContentJson, StringComparison.Ordinal);
    }

    private static async Task Denied(int status, Func<Task> action) => Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);
}

internal sealed class SharingFixture : IAsyncDisposable
{
    private SharingFixture(AtlasTestHost host) => Host = host;
    public AtlasTestHost Host { get; }
    public UserAccount Admin { get; private set; } = null!;
    public UserAccount WikiAdmin { get; private set; } = null!;
    public UserAccount Writer { get; private set; } = null!;
    public UserAccount Reader { get; private set; } = null!;
    public UserAccount Outsider { get; private set; } = null!;
    public Guid SpaceId { get; private set; }
    public Guid PageId { get; private set; }
    public static async Task<SharingFixture> CreateAsync(Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configure = null, bool isolated = false)
    {
        // isolated: for a test that creates a database trigger (DDL), which would block the shared database for everyone.
        var host = isolated ? await AtlasTestHost.CreateIsolatedAsync("atlas_sharing_", configure) /* own database: the test creates a trigger on PageAccessEvent (DDL) */ : await AtlasTestHost.CreateAsync("atlas_sharing_", configure);
        var fixture = new SharingFixture(host)
        {
            Admin = await host.SeedUserAsync("admin"), WikiAdmin = await host.SeedUserAsync("wikiadmin", true),
            Reader = await host.SeedUserAsync("reader"), Writer = await host.SeedUserAsync("writer"), Outsider = await host.SeedUserAsync("outsider")
        };
        fixture.SpaceId = await host.SeedSpaceAsync("Sharing", (fixture.Admin, SpaceRole.Admin), (fixture.Writer, SpaceRole.Writer), (fixture.Reader, SpaceRole.Reader));
        host.Actor.Current = fixture.Writer;
        var page = await host.InvokeAsync<PageDto>(new CreatePage(new(fixture.SpaceId, null, "Published", SharingSite.Rich("Published"), "Published")));
        fixture.PageId = page.Id;
        await host.InvokeAsync<PageDto>(new PublishPage(page.Id, new(page.Version)));
        host.Actor.Current = fixture.Admin;
        return fixture;
    }
    public Task<SharingPolicyDto> Enable()
    {
        // TestActorHolder is AsyncLocal: the async fixture factory's assignment does not flow back
        // to its caller. Set the actor in this synchronous entry point before starting the invocation.
        Host.Actor.Current = Admin;
        return Host.InvokeAsync<SharingPolicyDto>(new SaveSharingPolicy(SpaceId, new(true, null)));
    }
    public CreateShareLink Create(string name = "Review") => new(PageId, new(name, DateTimeOffset.UtcNow.AddDays(1)));
    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
