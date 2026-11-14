using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Vaults.Requests;
using Xunit;

namespace Atlas.Server.Tests.Platform;

/// <summary>
/// IntegrityRules (C# since 3.6, migration 2026110945 dropped the guard triggers): every NHibernate write that breaks an append-only or
/// no-delete rule throws with the trigger's message and the transaction keeps nothing. One case per kind of rule; the Hub's identity
/// rules are in HubMembershipPersistenceTests, the configuration version rule in TrustedAddressHandlerTests. Native SQL is held to the
/// same rules by IntegrityRulesConventionTests.
/// </summary>
public sealed class IntegrityRulesTests
{
    [PostgreSqlFact]
    public async Task An_access_history_interval_closes_once_and_is_never_rewritten_or_deleted()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_integrity_history_");
        var member = await host.SeedUserAsync("member");
        var group = await host.SeedGroupAsync("Tým", member);
        Guid id;
        using (var read = host.OpenSession())
        {
            id = (await read.QueryOver<GroupMember>().Where(x => x.GroupId == group).SingleOrDefaultAsync()).Id;
        }
        await RefusedAsync(host, IntegrityRules.HistoryImmutable, async s => (await s.GetAsync<GroupMember>(id))!.UserId = Guid.NewGuid());
        await RefusedAsync(host, IntegrityRules.HistoryNotDeleted, async s => await s.DeleteAsync(await s.GetAsync<GroupMember>(id)));
        await host.SeedAsync(async s => (await s.GetAsync<GroupMember>(id))!.ActiveTo = DateTime.UtcNow);
        await RefusedAsync(host, IntegrityRules.HistoryImmutable, async s => (await s.GetAsync<GroupMember>(id))!.ActiveTo = DateTime.UtcNow.AddDays(1));
        using var check = host.OpenSession();
        Assert.Equal(member.Id, (await check.GetAsync<GroupMember>(id))!.UserId);
    }

    [PostgreSqlFact]
    public async Task An_access_request_is_decided_once_and_never_deleted()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_integrity_request_");
        var requester = await host.SeedUserAsync("requester");
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var space = await host.SeedSpaceAsync("Trezory");
        var id = Guid.NewGuid();
        await host.SeedAsync(async s =>
        {
            var location = Guid.NewGuid();
            var vault = Guid.NewGuid();
            await s.CreateSQLQuery("""
                INSERT INTO "VaultLocation"("Id","Name","Kind","Root") VALUES(:location,:name,'local','/srv/keepass');
                INSERT INTO "Vault"("Id","SpaceId","Name","LocationId","FileName","State","CreatedBy","CreatedAt") VALUES(:vault,:space,'Hesla',:location,'a.kdbx','locked',:admin,now());
                """).SetParameter("location", location).SetParameter("name", TestLogin.For("Místní")).SetParameter("vault", vault).SetParameter("space", space)
                .SetParameter("admin", admin.Id).ExecuteUpdateAsync();
            await s.SaveAsync(new VaultAccessRequest { Id = id, VaultId = vault, VaultSpaceId = space, RequesterId = requester.Id, Level = 20,
                Reason = "Servis", State = VaultAccessRequest.Pending, CreatedAt = DateTime.UtcNow });
        });
        await RefusedAsync(host, "An access request is decided once and its request is immutable", async s => (await s.GetAsync<VaultAccessRequest>(id))!.Reason = "Jiný");
        await RefusedAsync(host, "Access requests cannot be deleted", async s => await s.DeleteAsync(await s.GetAsync<VaultAccessRequest>(id)));
        await host.SeedAsync(async s => Decide((await s.GetAsync<VaultAccessRequest>(id))!, VaultAccessRequest.Cancelled, requester.Id));
        await RefusedAsync(host, "An access request is decided once and its request is immutable",
            async s => Decide((await s.GetAsync<VaultAccessRequest>(id))!, VaultAccessRequest.Rejected, admin.Id));
    }

    [PostgreSqlFact]
    public async Task The_security_configuration_and_trusted_addresses_are_never_deleted_and_an_archived_address_stays_archived()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_integrity_security_");
        var address = new TrustedAddress { Id = Guid.NewGuid(), Purpose = TrustedAddress.NetworkPurpose, Name = "Kancelář", Kind = "lan", Range = "192.168.10.0/24",
            IsActive = true, ArchivedAt = DateTime.UtcNow };
        await host.SeedAsync(s => s.SaveAsync(address));
        await RefusedAsync(host, "The security configuration row cannot be deleted",
            async s => await s.DeleteAsync(await s.GetAsync<SecurityConfiguration>(SecurityConfiguration.SingletonId)));
        await RefusedAsync(host, "Security configuration versions increase by one",
            async s => (await s.GetAsync<SecurityConfiguration>(SecurityConfiguration.SingletonId))!.Version += 2);
        await RefusedAsync(host, "Trusted addresses are archived, not deleted", async s => await s.DeleteAsync(await s.GetAsync<TrustedAddress>(address.Id)));
        await RefusedAsync(host, "A trusted address keeps its purpose and an archived one stays archived",
            async s => (await s.GetAsync<TrustedAddress>(address.Id))!.ArchivedAt = null);
    }

    private static void Decide(VaultAccessRequest request, string state, Guid by)
    {
        request.State = state;
        request.DecidedBy = by;
        request.DecidedAt = DateTime.UtcNow;
    }

    /// <summary>History the session decides is never lost to an earlier flush: an account flushed before AccountHistory read its change
    /// is refused, and a page's access mode is compared with its current revision, so a flush in between still records it once.</summary>
    [PostgreSqlFact]
    public async Task A_flush_before_the_history_writer_loses_no_history()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_integrity_flush_");
        var user = await host.SeedUserAsync("user");
        var page = await PageAccess.ItemAccessSeed.PageAsync(host, await host.SeedSpaceAsync("Provoz"), user);
        async Task<long> CountAsync(string sql, Guid id)
        {
            using var read = host.OpenSession();
            return await read.CreateSQLQuery(sql).SetParameter("id", id).UniqueResultAsync<long>();
        }
        const string accountRevisions = """SELECT count(*) FROM "UserAccountRevision" WHERE "UserId"=:id""";
        const string modeRevisions = """SELECT count(*) FROM "PageAccessModeRevision" WHERE "PageId"=:id""";
        var (accounts, modes) = (await CountAsync(accountRevisions, user.Id), await CountAsync(modeRevisions, page));

        await RefusedAsync(host, AccountHistory.FlushedWithoutHistory, async s => (await s.GetAsync<UserAccount>(user.Id))!.DisplayName = "Jiné");
        await host.SeedAsync(async s =>
        {
            var account = (await s.GetAsync<UserAccount>(user.Id))!;
            account.DisplayName = "Jiné";
            await AccountHistory.RecordAsync(s, account, null, CancellationToken.None);
        });
        await host.SeedAsync(async s =>
        {
            var own = (await s.GetAsync<Page>(page))!;
            own.OwnAccess = true;
            await s.FlushAsync();
            await PageAccessModeHistory.RecordAsync(s, own, user.Id, CancellationToken.None);
            await PageAccessModeHistory.RecordAsync(s, own, user.Id, CancellationToken.None);
        });

        Assert.Equal((accounts + 1, modes + 1), (await CountAsync(accountRevisions, user.Id), await CountAsync(modeRevisions, page)));
    }

    private static async Task RefusedAsync(AtlasTestHost host, string message, Func<NHibernate.ISession, Task> change)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.SeedAsync(async session =>
        {
            await change(session);
            await session.FlushAsync();
        }));
        Assert.Equal(message, error.Message);
    }
}
