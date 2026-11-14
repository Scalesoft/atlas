using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Queries;
using Atlas.Server.Features.Vaults.References;
using Atlas.Server.Features.Vaults.Requests;
using Atlas.Server.Features.Vaults.Requests.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Vaults.VaultReleaseTests;

namespace Atlas.Server.Tests.Vaults;

/// <summary>Access requests (WP4 V5): request → approval → grant with its authorization, delegation bound, immutable history and
/// live events.</summary>
public sealed class AccessRequestTests
{
    [PostgreSqlFact]
    public async Task A_discoverer_asks_once_and_the_managers_are_told()
    {
        await using var f = await VaultFixture.CreateAsync(release: new FakeReleasePolicy());
        var events = f.Events;
        var (vault, router, _) = await AttachAsync(f);
        var asker = await f.Host.SeedUserAsync("asker");
        var stranger = await f.Host.SeedUserAsync("stranger");
        var reader = await f.Host.SeedUserAsync("reader");
        var groupManager = await f.Host.SeedUserAsync("group-manager");
        var group = await f.Host.SeedGroupAsync("Správci", groupManager);
        await f.As(f.SpaceAdmin, new SetVaultGrant(vault, new UpdateVaultGrant(null, group, "manage", null)));
        await Grant(f, vault, asker, "discover");
        await Grant(f, vault, reader, "read");

        await Denied(404, () => f.As<AccessRequestView>(stranger, new RequestSecretAccess(router, new CreateAccessRequest("read", "Potřebuji"))));
        await Denied(404, () => f.As<AccessRequestView>(stranger, new RequestVaultAccess(vault, new CreateAccessRequest("read", "Potřebuji"))));
        await Denied(400, () => f.As<AccessRequestView>(asker, new RequestSecretAccess(router, new CreateAccessRequest("read", "  "))));
        await Denied(400, () => f.As<AccessRequestView>(asker, new RequestSecretAccess(router, new CreateAccessRequest("read", new string('x', 501)))));
        await Denied(400, () => f.As<AccessRequestView>(asker, new RequestSecretAccess(router, new CreateAccessRequest("manage", "Potřebuji"))));
        Assert.Equal("access_already_granted",
            (await Denied(409, () => f.As<AccessRequestView>(reader, new RequestSecretAccess(router, new CreateAccessRequest("read", "Potřebuji"))))).Code);

        var request = await f.As<AccessRequestView>(asker, new RequestSecretAccess(router, new CreateAccessRequest("read", "  Servis routeru ")));
        Assert.Equal(("pending", "read", "Servis routeru", router, "Router", asker.Id, false),
            (request.State, request.Level, request.Reason, request.SecretId!.Value, request.SecretTitle, request.RequesterId, request.CanManage));
        var again = await f.As<AccessRequestView>(asker, new RequestSecretAccess(router, new CreateAccessRequest("read", "Jiný důvod")));
        Assert.Equal((request.Id, "Servis routeru"), (again.Id, again.Reason));
        // The whole vault is a different target.
        var whole = await f.As<AccessRequestView>(asker, new RequestVaultAccess(vault, new CreateAccessRequest("read", "Celý trezor")));
        Assert.NotEqual(request.Id, whole.Id);
        Assert.Null(whole.SecretId);

        var (users, change) = Assert.Single(events.RequestChanges, e => e.Change.RequestId == request.Id);
        Assert.Equal(new AccessRequestChanged(request.Id, router, vault, "pending"), change);
        // WikiAdmin, the space Admin, the manager through a group and the requester; not the reader or the stranger. Every other
        // wiki administrator of the shared host is told too: the assertion keeps to this test's accounts.
        var own = new[] { f.Admin.Id, f.SpaceAdmin.Id, groupManager.Id, asker.Id, reader.Id, stranger.Id };
        Assert.Equal(new[] { f.Admin.Id, f.SpaceAdmin.Id, groupManager.Id, asker.Id }.Order(), users.Where(own.Contains).Order());

        // Lists: managers see what they can decide, the requester their own.
        Assert.Equal([whole.Id, request.Id], (await f.As<AccessRequestList>(f.SpaceAdmin, new ListAccessRequests(null, null))).Items.Select(r => r.Id));
        Assert.All((await f.As<AccessRequestList>(groupManager, new ListAccessRequests("pending", vault))).Items, r => Assert.True(r.CanManage));
        Assert.Empty((await f.As<AccessRequestList>(reader, new ListAccessRequests("all", null))).Items);
        Assert.Empty((await f.As<AccessRequestList>(asker, new ListAccessRequests("all", null))).Items);
        Assert.Equal(2, (await f.As<AccessRequestList>(asker, new ListMyAccessRequests())).Items.Count);
        await Denied(400, () => f.As<AccessRequestList>(f.SpaceAdmin, new ListAccessRequests("open", null)));
    }

    [PostgreSqlFact]
    public async Task Approval_writes_a_bounded_grant_through_the_grant_path()
    {
        await using var f = await VaultFixture.CreateAsync(release: new FakeReleasePolicy());
        var events = f.Events;
        var (vault, router, mail) = await AttachAsync(f);
        var asker = await f.Host.SeedUserAsync("asker");
        var delegate_ = await f.Host.SeedUserAsync("delegate");
        await Grant(f, vault, asker, "discover");
        var delegateUntil = DateTimeOffset.UtcNow.AddDays(5);
        await f.As(f.SpaceAdmin, new SetVaultGrant(vault, new UpdateVaultGrant(delegate_.Id, null, "manage", null, null, delegateUntil)));

        var request = await f.As<AccessRequestView>(asker, new RequestSecretAccess(router, new CreateAccessRequest("read", "Servis")));
        // Neither the requester nor someone who only discovers may decide.
        await Denied(403, () => f.As<AccessRequestView>(asker, new ApproveAccessRequest(request.Id, null)));
        var nobody = await f.Host.SeedUserAsync("nobody");
        await Denied(404, () => f.As<AccessRequestView>(nobody, new ApproveAccessRequest(request.Id, null)));
        await Denied(404, () => f.As<AccessRequestView>(f.SpaceAdmin, new ApproveAccessRequest(Guid.NewGuid(), null)));

        var before = DateTime.UtcNow;
        var approved = await f.As<AccessRequestView>(f.SpaceAdmin, new ApproveAccessRequest(request.Id, null));
        Assert.Equal(("approved", f.SpaceAdmin.Id), (approved.State, approved.DecidedBy!.Value));
        Assert.InRange(approved.GrantExpiresAt!.Value, before.AddDays(30).AddSeconds(-5), DateTime.UtcNow.AddDays(30).AddSeconds(5));
        using (var session = f.Host.OpenSession())
        {
            var grant = await session.GetAsync<ResourceGrant>(approved.GrantId!.Value);
            Assert.Equal((router, asker.Id, 20, (DateTime?)approved.GrantExpiresAt, (DateTime?)null), (grant.SecretId!.Value, grant.UserId!.Value, grant.Role, grant.ExpiresAt, grant.ActiveTo));
            var actions = await session.CreateSQLQuery("""
                SELECT "Action" || '|' || CAST("ActorId" AS text) FROM "AccessAuditView" WHERE "AccessRequestId" = :id OR "ResourceGrantId" = :grant ORDER BY "Id"
                """).SetParameter("id", request.Id).SetParameter("grant", grant.Id).ListAsync<string>();
            Assert.Equal([$"access_request.created|{asker.Id}", $"resource_grant.added|{f.SpaceAdmin.Id}", $"access_request.approved|{f.SpaceAdmin.Id}"], actions);
        }
        Assert.Equal("r0uter-pw", (await Release(f, asker, router)).Values!.Values.Single().Value);
        Assert.Equal("access_request_decided", (await Denied(409, () => f.As<AccessRequestView>(f.SpaceAdmin, new ApproveAccessRequest(request.Id, null)))).Code);
        Assert.Equal("approved", events.RequestChanges.Last(e => e.Change.RequestId == request.Id).Change.State);

        // A temporary manager's default approval ends with its own authority; a later explicit end is refused.
        var mailRequest = await f.As<AccessRequestView>(asker, new RequestSecretAccess(mail, new CreateAccessRequest("read", "Pošta")));
        await Denied(403, () => f.As<AccessRequestView>(delegate_, new ApproveAccessRequest(mailRequest.Id, delegateUntil.AddDays(1))));
        await Denied(400, () => f.As<AccessRequestView>(delegate_, new ApproveAccessRequest(mailRequest.Id, DateTimeOffset.UtcNow.AddMinutes(-1))));
        var bounded = await f.As<AccessRequestView>(delegate_, new ApproveAccessRequest(mailRequest.Id, null));
        Assert.InRange(bounded.GrantExpiresAt!.Value, delegateUntil.UtcDateTime.AddSeconds(-1), delegateUntil.UtcDateTime);

        // Approval replaces a lower current grant of the requester (CAS on its version) with Čte.
        var vaultRequest = await f.As<AccessRequestView>(asker, new RequestVaultAccess(vault, new CreateAccessRequest("read", "Celý")));
        var vaultApproved = await f.As<AccessRequestView>(f.Admin, new ApproveAccessRequest(vaultRequest.Id, DateTimeOffset.UtcNow.AddDays(2)));
        using (var session = f.Host.OpenSession())
        {
            var grant = await session.GetAsync<ResourceGrant>(vaultApproved.GrantId!.Value);
            Assert.Equal((20, (Guid?)null, (DateTime?)null), (grant.Role, grant.SecretId, grant.ActiveTo));
            Assert.InRange(grant.ExpiresAt!.Value, DateTime.UtcNow.AddDays(2).AddMinutes(-1), DateTime.UtcNow.AddDays(2));
        }

        // A request that a longer current grant already covers keeps that grant as it is: never lowered or shortened.
        var covered = await f.Host.SeedUserAsync("covered");
        await Grant(f, vault, covered, "discover");
        var coveredRequest = await f.As<AccessRequestView>(covered, new RequestVaultAccess(vault, new CreateAccessRequest("read", "Celý")));
        await f.As(f.SpaceAdmin, new SetVaultGrant(vault, new UpdateVaultGrant(covered.Id, null, "manage", await VersionAsync(f, vault, covered))));
        var coveredApproved = await f.As<AccessRequestView>(f.SpaceAdmin, new ApproveAccessRequest(coveredRequest.Id, null));
        Assert.Null(coveredApproved.GrantExpiresAt);
        using (var session = f.Host.OpenSession())
        {
            var grant = await session.GetAsync<ResourceGrant>(coveredApproved.GrantId!.Value);
            Assert.Equal((40, (DateTime?)null, (DateTime?)null), (grant.Role, grant.ExpiresAt, grant.ActiveTo));
        }
    }

    [PostgreSqlFact]
    public async Task Rejection_and_cancellation_close_a_request_once()
    {
        await using var f = await VaultFixture.CreateAsync(release: new FakeReleasePolicy());
        var events = f.Events;
        var (vault, router, mail) = await AttachAsync(f);
        var asker = await f.Host.SeedUserAsync("asker");
        var other = await f.Host.SeedUserAsync("other");
        await Grant(f, vault, asker, "discover");
        await Grant(f, vault, other, "discover");

        var first = await f.As<AccessRequestView>(asker, new RequestSecretAccess(router, new CreateAccessRequest("read", "Servis")));
        await Denied(400, () => f.As<AccessRequestView>(f.SpaceAdmin, new RejectAccessRequest(first.Id, new string('n', 501))));
        await Denied(403, () => f.As<AccessRequestView>(other, new RejectAccessRequest(first.Id, null)));
        var rejected = await f.As<AccessRequestView>(f.SpaceAdmin, new RejectAccessRequest(first.Id, " Použijte servisní účet "));
        Assert.Equal(("rejected", "Použijte servisní účet", (Guid?)null), (rejected.State, rejected.Note, rejected.GrantId));
        // The reference the editor reloads tells the requester the outcome of their latest request.
        var refused = await f.As<SecretReferenceView>(asker, new GetSecretReference(router));
        Assert.Equal((false, true), (refused.RequestPending, refused.RequestRefused));
        await Denied(409, () => f.As<AccessRequestView>(asker, new CancelAccessRequest(first.Id)));
        // A rejection is not a deny, and a new request may follow.
        var second = await f.As<AccessRequestView>(asker, new RequestSecretAccess(router, new CreateAccessRequest("read", "Znovu")));
        Assert.NotEqual(first.Id, second.Id);
        var pending = await f.As<SecretReferenceView>(asker, new GetSecretReference(router));
        Assert.Equal((true, false), (pending.RequestPending, pending.RequestRefused));

        var mailRequest = await f.As<AccessRequestView>(asker, new RequestSecretAccess(mail, new CreateAccessRequest("read", "Pošta")));
        await Denied(404, () => f.As<AccessRequestView>(other, new CancelAccessRequest(mailRequest.Id)));
        var cancelled = await f.As<AccessRequestView>(asker, new CancelAccessRequest(mailRequest.Id));
        Assert.Equal(("cancelled", asker.Id), (cancelled.State, cancelled.DecidedBy!.Value));
        await Denied(409, () => f.As<AccessRequestView>(f.SpaceAdmin, new ApproveAccessRequest(mailRequest.Id, null)));
        Assert.Equal(["pending", "rejected", "pending", "pending", "cancelled"], events.RequestChanges.Where(e => e.Change.VaultId == vault).Select(e => e.Change.State));
        Assert.Single((await f.As<AccessRequestList>(f.SpaceAdmin, new ListAccessRequests(null, vault))).Items);
        Assert.Equal(3, (await f.As<AccessRequestList>(f.SpaceAdmin, new ListAccessRequests("all", vault))).Items.Count);

        // The history is immutable for the application too (IntegrityRules).
        const string Decided = "An access request is decided once and its request is immutable";
        await IntegrityRefusal.AssertAsync(f.Host, Decided, async s =>
        {
            var reopened = (await s.GetAsync<VaultAccessRequest>(first.Id))!;
            (reopened.State, reopened.DecidedBy, reopened.DecidedAt) = (VaultAccessRequest.Pending, null, null);
        });
        await IntegrityRefusal.AssertAsync(f.Host, Decided, async s => (await s.GetAsync<VaultAccessRequest>(second.Id))!.Reason = "jiný");
        await IntegrityRefusal.AssertAsync(f.Host, "Access requests cannot be deleted",
            async s => await s.DeleteAsync(await s.GetAsync<VaultAccessRequest>(second.Id)));
    }

    private static async Task<string?> VersionAsync(VaultFixture f, Guid vault, UserAccount user)
    {
        var state = await f.As<VaultAccessState>(f.SpaceAdmin, new GetVaultAccess(vault));
        return state.People.SingleOrDefault(p => p.UserId == user.Id)?.Sources.SingleOrDefault(s => s.GroupId is null && s.Kind == "vault")?.Version;
    }
}
