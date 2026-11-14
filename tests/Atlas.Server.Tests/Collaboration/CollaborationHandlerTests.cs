using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NHibernate;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Collaboration;

public sealed class CollaborationHandlerTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Checkpoints_validate_the_durable_projection_advance_the_base_revision_and_reset_the_generation()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        await f.Append(opened.DocumentId, Guid.NewGuid(), [1]);
        using (var session = f.Host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var page = await session.GetAsync<Page>(f.Page.Id, LockMode.Upgrade);
            var current = await session.QueryOver<PageRevision>().Where(x => x.PageId == f.Page.Id && x.ActiveTo == null).Select(x => x.Id).SingleOrDefaultAsync<Guid>();
            var pending = await Assert.ThrowsAsync<ApiException>(() => new CollaborationCheckpoints(session).ValidateCheckpointAsync(page, opened.Generation, current, null, default));
            Assert.Equal(409, pending.Status);
            Assert.Equal("Společné změny se ještě ukládají.", pending.Message);
            Assert.Equal("collaboration_pending", pending.Code);
            await transaction.CommitAsync();
        }
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(1), default);
        using (var session = f.Host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var checkpoints = new CollaborationCheckpoints(session);
            var page = await session.GetAsync<Page>(f.Page.Id, LockMode.Upgrade);
            var current = await session.QueryOver<PageRevision>().Where(x => x.PageId == f.Page.Id && x.ActiveTo == null).Select(x => x.Id).SingleOrDefaultAsync<Guid>();
            var checkpoint = await checkpoints.ValidateCheckpointAsync(page, opened.Generation, current, 1, default);
            Assert.Equal(1, checkpoint.Sequence);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => checkpoints.ValidateCheckpointAsync(page, opened.Generation, current, 0, default))).Status);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => checkpoints.ValidateCheckpointAsync(page, Guid.NewGuid(), current, null, default))).Status);
            var revision = await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync();
            await checkpoints.AdvanceCheckpointAsync(page, revision, default, markSaved: true);
            await transaction.CommitAsync();
        }
        using (var read = f.Host.OpenSession())
        {
            var state = await read.GetAsync<PageCollaboration>(f.Page.Id);
            Assert.Equal(opened.Generation, state.Generation);
            Assert.Equal(1, state.LastSavedSequence);
        }
        using (var session = f.Host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var page = await session.GetAsync<Page>(f.Page.Id, LockMode.Upgrade);
            var revision = await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync();
            await new CollaborationCheckpoints(session).ResetAsync(page, revision, default);
            await transaction.CommitAsync();
        }
        using (var read = f.Host.OpenSession())
        {
            var state = await read.GetAsync<PageCollaboration>(f.Page.Id);
            Assert.NotEqual(opened.Generation, state.Generation);
            Assert.Equal(0, state.Sequence);
            Assert.Null(state.State);
            // The map remark requires page AND generation: octet_length detoasts every row an unfiltered scan touches.
            // Filtering on the NEW generation is what makes this meaningful: had ResetAsync left rows behind, the
            // migration-017 ON UPDATE CASCADE would have moved them onto this generation and they would be counted.
            Assert.Equal(0, await read.QueryOver<CollaborationUpdateSize>().Where(x => x.PageId == f.Page.Id && x.Generation == state.Generation).RowCountAsync());
        }
    }
    [PostgreSqlFact]
    public async Task Relay_authorization_refreshes_the_actor_so_a_rotated_stamp_expires_the_session()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        f.Host.Actor.Current = f.Writer;
        var denied = await Assert.ThrowsAsync<ApiException>(() => f.Host.InvokeAsync<Guid>(new ProbeStaleCollaborator(f.Page.Id, f.Writer.Id)));
        Assert.Equal(401, denied.Status);
        Assert.Equal("Přihlášení vypršelo.", denied.Message);
    }

    [PostgreSqlFact]
    public async Task Authentication_maps_an_expired_account_to_no_collaborator()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var sessions = new CollaborationSessions();
        var authenticator = new AtlasCollaborationAuthenticator(f.Host.Services.GetRequiredService<IServiceScopeFactory>(), sessions);
        Assert.NotNull(await authenticator.AuthenticateAsync(Handshake(f.Writer.Id.ToString(), f.Writer.SecurityStamp), default));
        // A rotated stamp makes the handler throw 401; the authenticator answers null so the hub just refuses the connection.
        Assert.Null(await authenticator.AuthenticateAsync(Handshake(f.Writer.Id.ToString(), "rotated"), default));
        Assert.Null(await authenticator.AuthenticateAsync(Handshake(Guid.NewGuid().ToString(), f.Writer.SecurityStamp), default));
        Assert.Null(await authenticator.AuthenticateAsync(new DefaultHttpContext(), default));
    }

    private static DefaultHttpContext Handshake(string userId, string stamp)
    {
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId), new Claim("stamp", stamp), new Claim("atlas_session", Guid.NewGuid().ToString("N"))], "test"))
        };
    }
}
