using NHibernate.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Atlas.Server.Features.Administration;
using Atlas.Server.Features.Administration.Commands;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Administration;

public sealed class GrantValidityTests
{
    [PostgreSqlFact]
    public async Task Effective_roles_filter_whole_direct_and_group_grants_and_keep_legacy_unbounded_grants()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_grant_validity_");
        var member = await host.SeedUserAsync("member");
        var group = await host.SeedGroupAsync("editors", member);
        var legacy = await host.SeedSpaceAsync("legacy", (member, SpaceRole.Reader));
        var future = await host.SeedSpaceAsync("future");
        var expired = await host.SeedSpaceAsync("expired");
        var mixed = await host.SeedSpaceAsync("mixed", (member, SpaceRole.Writer));
        var now = DateTime.UtcNow;
        await host.SeedAsync(async session =>
        {
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = future, UserId = member.Id, Role = 3, StartAt = now.AddHours(1) });
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = expired, UserId = member.Id, Role = 3, ExpiresAt = now.AddHours(-1) });
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = mixed, GroupId = group, Role = 3, ExpiresAt = now.AddHours(-1) });
        });
        using var session = host.OpenSession();
        var roles = new SpaceRoles(session);
        var all = await roles.EffectiveAsync(member, CancellationToken.None);
        Assert.Equal(SpaceRole.Reader, all[legacy]);
        Assert.Equal(SpaceRole.Writer, all[mixed]);
        Assert.False(all.ContainsKey(future));
        Assert.False(all.ContainsKey(expired));
        Assert.Null(await roles.EffectiveAsync(member, future, CancellationToken.None));
        Assert.Null(await roles.EffectiveAsync(member, expired, CancellationToken.None));
        Assert.Equal(SpaceRole.Writer, await roles.EffectiveAsync(member, mixed, CancellationToken.None));
        var effective = await session.CreateSQLQuery("SELECT \"SpaceId\" FROM \"EffectiveSpaceGrant\" WHERE \"UserId\"=:id")
            .SetParameter("id", member.Id).ListAsync<Guid>();
        Assert.Equal(new[] { legacy, mixed }.Order(), effective.Order());
    }

    [PostgreSqlFact]
    public async Task Authority_uses_only_current_admin_grants_and_distinguishes_denial_from_unbounded()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_grant_authority_");
        var member = await host.SeedUserAsync("member");
        var wikiAdmin = await host.SeedUserAsync("wiki", isWikiAdmin: true);
        var group = await host.SeedGroupAsync("admins", member);
        var laterGroup = await host.SeedGroupAsync("later-admins", member);
        var space = await host.SeedSpaceAsync("bounded");
        var readerSpace = await host.SeedSpaceAsync("reader", (member, SpaceRole.Reader));
        var permanent = await host.SeedSpaceAsync("permanent", (member, SpaceRole.Admin));
        var expiry = new DateTime(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await host.SeedAsync(async session =>
        {
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = space, UserId = member.Id, Role = 3, ExpiresAt = expiry });
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = space, GroupId = group, Role = 3, StartAt = expiry.AddDays(1) });
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = space, GroupId = laterGroup, Role = 3, ExpiresAt = expiry.AddDays(2) });
        });
        using var session = host.OpenSession();
        var roles = new SpaceRoles(session);
        Assert.Equal((true, (DateTime?)expiry.AddDays(2)), await roles.AdminAuthorityAsync(member, space, CancellationToken.None));
        Assert.Equal((false, (DateTime?)null), await roles.AdminAuthorityAsync(member, readerSpace, CancellationToken.None));
        Assert.Equal((true, (DateTime?)null), await roles.AdminAuthorityAsync(member, permanent, CancellationToken.None));
        Assert.Equal((true, (DateTime?)null), await roles.AdminAuthorityAsync(wikiAdmin, space, CancellationToken.None));
        member.IsActive = false;
        Assert.Equal((false, (DateTime?)null), await roles.AdminAuthorityAsync(member, permanent, CancellationToken.None));
        Assert.Empty(await roles.EffectiveAsync(member, CancellationToken.None));
        Assert.Null(await roles.EffectiveAsync(member, permanent, CancellationToken.None));
    }

    [PostgreSqlFact]
    public async Task Changing_only_validity_versions_the_grant_preserves_audit_and_noop_requires_identical_bounds()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        var start = new DateTimeOffset(2090, 1, 1, 4, 0, 0, TimeSpan.FromHours(2));
        var end = start.AddDays(1);
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Reader", null, start, end)));
        var first = await MemberGrant(f);
        Assert.Equal(start.UtcDateTime, first.StartAt);
        Assert.Equal(end.UtcDateTime, first.ExpiresAt);
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Reader", first.Version, start, end)));
        Assert.Equal(first.Version, (await MemberGrant(f)).Version);
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Reader", first.Version, start, end.AddDays(1))));
        var second = await MemberGrant(f);
        Assert.NotEqual(first.Version, second.Version);
        await AdministrationFixture.Denied(409, () => f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Writer", first.Version))));
        using var session = f.Host.OpenSession();
        var history = await session.QueryOver<SpaceGrant>().Where(x => x.UserId == f.Member.Id).OrderBy(x => x.ActiveFrom).Asc.ListAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal(history[0].ActiveTo, history[1].ActiveFrom);
        Assert.Equal(end.UtcDateTime, history[0].ExpiresAt);
        Assert.Equal(end.AddDays(1).UtcDateTime, history[1].ExpiresAt);
        Assert.Equal(3, await session.CreateSQLQuery("SELECT count(*) FROM \"AccessAuditEvent\" e JOIN \"SpaceGrant\" g ON g.\"Id\"=e.\"SpaceGrantId\" WHERE g.\"UserId\"=:id")
            .SetParameter("id", f.Member.Id).UniqueResultAsync<long>());
        await IntegrityRefusal.AssertAsync(f.Host, IntegrityRules.HistoryImmutable, async s => (await s.GetAsync<SpaceGrant>(history[1].Id))!.ExpiresAt = null);
    }

    [PostgreSqlFact]
    public async Task Last_permanent_admin_cannot_be_replaced_by_a_temporary_or_future_assignment()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        var expiry = DateTimeOffset.UtcNow.AddDays(2);
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Admin", null, null, expiry)));
        var owner = Assert.Single((await f.As<SpaceAccessSummary>(f.Admin, new GetSpaceAccess(f.SpaceId))).Grants, x => x.UserId == f.Admin.Id);
        await AdministrationFixture.Denied(409, () => f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Admin.Id, null, "Admin", owner.Version, null, expiry))));
        await AdministrationFixture.Denied(409, () => f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Admin.Id, null, "Reader", owner.Version))));
        var member = await MemberGrant(f);
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Admin", member.Version, expiry, null)));
        await AdministrationFixture.Denied(409, () => f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Admin.Id, null, null, owner.Version))));
        member = await MemberGrant(f);
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Admin", member.Version)));
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Admin.Id, null, "Reader", owner.Version)));
    }

    [PostgreSqlFact]
    public async Task Temporary_admin_cannot_delegate_beyond_own_current_authority()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        var target = await f.Host.SeedUserAsync("target");
        var expiry = new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Admin", null, null, expiry)));
        await AdministrationFixture.Denied(403, () => f.As(f.Member, new SetSpaceGrant(f.SpaceId, new(target.Id, null, "Reader", null))));
        await AdministrationFixture.Denied(403, () => f.As(f.Member, new SetSpaceGrant(f.SpaceId, new(target.Id, null, "Reader", null, null, expiry.AddSeconds(1)))));
        await f.As(f.Member, new SetSpaceGrant(f.SpaceId, new(target.Id, null, "Reader", null, null, expiry)));
        var grant = Assert.Single((await f.As<SpaceAccessSummary>(f.Admin, new GetSpaceAccess(f.SpaceId))).Grants, x => x.UserId == target.Id);
        Assert.Equal(expiry.UtcDateTime, grant.ExpiresAt);
        await f.As(f.Member, new SetSpaceGrant(f.SpaceId, new(target.Id, null, null, grant.Version)));
    }

    [PostgreSqlFact]
    public async Task Invalid_or_infinite_business_ranges_are_rejected_by_API_and_database()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        var now = new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var (start, end) in new (DateTimeOffset?, DateTimeOffset?)[] { (now, now), (now, now.AddTicks(1)), (now, now.AddDays(-1)), (DateTimeOffset.MinValue, null), (DateTimeOffset.MinValue.AddTicks(1), null), (null, DateTimeOffset.MaxValue) })
            await AdministrationFixture.Denied(400, () => f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Reader", null, start, end))));
        foreach (var bounds in new[] { "now(),now()", "now()+interval '1 day',now()", "'infinity',NULL", "NULL,'-infinity'" })
        {
            var error = await Assert.ThrowsAsync<GenericADOException>(() => f.Host.ExecuteAsync($"INSERT INTO \"SpaceGrant\"(\"Id\",\"SpaceId\",\"UserId\",\"Role\",\"StartAt\",\"ExpiresAt\") SELECT gen_random_uuid(),\"SpaceId\",\"UserId\",1,{bounds} FROM \"SpaceGrant\" WHERE \"UserId\"=:id", f.Admin.Id));
            Assert.Equal("23514", Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
    }

    [PostgreSqlFact]
    public async Task Submicrosecond_input_roundtrips_to_a_true_noop()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        var start = new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(19);
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Reader", null, start, start.AddDays(1))));
        var first = await MemberGrant(f);
        Assert.Equal(start.UtcDateTime.AddTicks(-9), first.StartAt);
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Reader", first.Version, start, start.AddDays(1))));
        Assert.Equal(first.Version, (await MemberGrant(f)).Version);
    }

    [PostgreSqlFact]
    public async Task New_or_changed_bounds_must_end_in_the_future_while_lapsed_bounds_that_are_only_resent_stay()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        const string lapsedEnd = "Konec platnosti už uplynul. Zvolte datum a čas v budoucnosti.";
        var past = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await AdministrationFixture.Denied(400, lapsedEnd, () => f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Reader", null, null, past))));
        await f.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = f.Member.Id, Role = 1, ExpiresAt = past.UtcDateTime }));
        var lapsed = await MemberGrant(f);
        // Resent unchanged (the no-op) or with another role, the stored lapsed bounds stay; moving them to another past instant does not.
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Reader", lapsed.Version, null, past)));
        Assert.Equal(lapsed.Version, (await MemberGrant(f)).Version);
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Writer", lapsed.Version, null, past)));
        var writer = await MemberGrant(f);
        Assert.Equal(("Writer", (DateTime?)past.UtcDateTime), (writer.Role, writer.ExpiresAt));
        await AdministrationFixture.Denied(400, lapsedEnd, () => f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Writer", writer.Version, null, past.AddDays(1)))));
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, null, writer.Version)));
    }

    [PostgreSqlFact]
    public async Task SQL_effective_view_has_inclusive_start_and_exclusive_expiry_at_the_statement_boundary()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_grant_edges_");
        var starting = await host.SeedUserAsync("starting");
        var expiring = await host.SeedUserAsync("expiring");
        var space = await host.SeedSpaceAsync("edges");
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""
            DO $test$
            BEGIN
                INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","Role","StartAt","ExpiresAt") VALUES
                    (gen_random_uuid(),'{space}','{starting.Id}',1,statement_timestamp(),NULL),
                    (gen_random_uuid(),'{space}','{expiring.Id}',3,NULL,statement_timestamp());
                IF NOT EXISTS (SELECT 1 FROM "EffectiveSpaceGrant" WHERE "UserId"='{starting.Id}') THEN
                    RAISE EXCEPTION 'Start boundary must be inclusive';
                END IF;
                IF EXISTS (SELECT 1 FROM "EffectiveSpaceGrant" WHERE "UserId"='{expiring.Id}') THEN
                    RAISE EXCEPTION 'Expiry boundary must be exclusive';
                END IF;
            END $test$;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    [PostgreSqlFact]
    public async Task Migration_preserves_current_and_closed_legacy_history_and_default_access()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_grant_upgrade_");
        await using var connection = new NpgsqlConnection(schema.ConnectionString);
        await connection.OpenAsync();
        async Task<object?> Execute(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return await command.ExecuteScalarAsync();
        }
        await Execute("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0]))).Where(x => x.Version < 2026092502).OrderBy(x => x.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await Execute(await reader.ReadToEndAsync());
            await Execute($"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
        await Execute("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES ('00000000-0000-0000-0000-000000000001','legacy','Legacy',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES ('00000000-0000-0000-0000-000000000002','Legacy','book-open','blue');
            INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","Role","ActiveFrom","ActiveTo") VALUES
                ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001',1,'2020-01-01T00:00:00Z','2021-01-01T00:00:00Z'),
                ('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001',3,'2021-01-01T00:00:00Z',NULL);
            """);
        var before = await Execute("SELECT jsonb_agg(to_jsonb(g) ORDER BY \"Id\")::text FROM \"SpaceGrant\" g");
        await AtlasMigrations.MigrateAsync(schema.ConnectionString);
        Assert.Equal(before, await Execute("SELECT jsonb_agg(to_jsonb(g)-'StartAt'-'ExpiresAt' ORDER BY \"Id\")::text FROM \"SpaceGrant\" g"));
        Assert.Equal(2L, await Execute("SELECT count(*) FROM \"SpaceGrant\" WHERE \"StartAt\" IS NULL AND \"ExpiresAt\" IS NULL"));
        Assert.Equal(1L, await Execute("SELECT count(*) FROM \"EffectiveSpaceGrant\""));
        await AtlasMigrations.MigrateAsync(schema.ConnectionString);
        Assert.Equal(2L, await Execute("SELECT count(*) FROM \"SpaceGrant\""));
    }

    [PostgreSqlFact]
    public async Task Expired_and_future_assignments_still_enforce_current_subject_uniqueness()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        var future = DateTimeOffset.UtcNow.AddDays(1);
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Reader", null, future, null)));
        foreach (var bounds in new[] { "NULL,now()-interval '1 day'", "now()+interval '2 days',NULL" })
        {
            var error = await Assert.ThrowsAsync<GenericADOException>(() => f.Host.ExecuteAsync($"INSERT INTO \"SpaceGrant\"(\"Id\",\"SpaceId\",\"UserId\",\"Role\",\"StartAt\",\"ExpiresAt\") SELECT gen_random_uuid(),\"SpaceId\",\"UserId\",1,{bounds} FROM \"SpaceGrant\" WHERE \"UserId\"=:id", f.Member.Id));
            Assert.Contains(Assert.IsType<PostgresException>(error.InnerException).SqlState, new[] { "23505", "23P01" });
        }
    }

    private static async Task<GrantSummary> MemberGrant(AdministrationFixture f)
        => Assert.Single((await f.As<SpaceAccessSummary>(f.Admin, new GetSpaceAccess(f.SpaceId))).Grants, x => x.UserId == f.Member.Id);

    [PostgreSqlFact]
    public async Task Permanent_admin_invariant_uses_the_same_database_clock_as_authorization()
    {
        await using var f = await AdministrationFixture.CreateAsync("creates a function statement_timestamp() in the schema (DDL, rolled back with the transaction)");
        var space = await f.Host.SeedSpaceAsync("clock");
        var version = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await f.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant
        {
            Id = Guid.NewGuid(), SpaceId = space, UserId = f.Admin.Id, Role = 3,
            ActiveFrom = version, StartAt = DateTime.UtcNow.AddHours(12)
        }));
        f.Host.Actor.Current = f.Admin;
        using var scope = f.Host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        using var transaction = session.BeginTransaction();
        // This disposable schema/transaction supplies a database clock ahead of the application.
        await session.CreateSQLQuery("CREATE FUNCTION statement_timestamp() RETURNS timestamptz LANGUAGE SQL STABLE AS $$ SELECT pg_catalog.statement_timestamp() + interval '1 day' $$").ExecuteUpdateAsync();
        await session.CreateSQLQuery("SELECT set_config('search_path', quote_ident(current_schema()) || ',pg_catalog,public', true)").UniqueResultAsync<string>();
        var handler = new SetSpaceGrantHandler(session, scope.ServiceProvider.GetRequiredService<AdministrationAccess>(), new SpaceRoles(session),
            scope.ServiceProvider.GetRequiredService<AccessChanges>(), new PermitSecurityChanges());
        await AdministrationFixture.Denied(409, () => handler.Handle(new SetSpaceGrant(space, new(f.Admin.Id, null, null, AdministrationVersion.Format(version))), CancellationToken.None));
    }
}
