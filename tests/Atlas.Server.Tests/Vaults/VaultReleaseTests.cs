using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.AccessAudit.Contracts;
using Atlas.Server.Features.AccessAudit.Queries;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.Vaults.Audit;
using Atlas.Server.Features.Vaults.Audit.Queries;
using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Queries;
using Atlas.Server.Features.Vaults.Release;
using Atlas.Server.Features.Vaults.Requests;
using Xunit;

namespace Atlas.Server.Tests.Vaults;

/// <summary>Audited value release (WP4 V5) over real PostgreSQL, the vault runtime and a switchboard release policy: the order
/// authorize → policy → re-authentication → read → durable audit → value, every denial audited, the rate limit, the vault audit
/// query and live revocation of released values.</summary>
public sealed class VaultReleaseTests
{
    private const string Fixture = "argon2d-aes-gzip.kdbx";

    [PostgreSqlFact]
    public async Task Allowed_release_returns_one_field_after_its_audit_row_committed()
    {
        var policy = new FakeReleasePolicy { Decision = new ReleaseDecision("outsideTrustedNetworks", "vault", false, true, "Kancelář") };
        await using var f = await VaultFixture.CreateAsync(release: policy);
        var (vault, router, mail) = await AttachAsync(f);
        var reader = await f.Host.SeedUserAsync("reader");
        await Grant(f, vault, reader, "read");

        var (value, reauth) = await Release(f, reader, router);
        Assert.Null(reauth);
        Assert.Equal("r0uter-pw", value!.Values["password"]);
        Assert.Equal(60, value.ExpiresInSeconds);
        Assert.Equal("admin", (await Release(f, reader, router, "userName", "copy")).Values!.Values.Single().Value);
        Assert.Equal("https://mail.example.test", (await Release(f, reader, mail, "url")).Values!.Values.Single().Value);
        // The row is committed and complete when the value is returned; it names the field, never the value.
        var rows = await AuditRows(f, router);
        Assert.Equal(["released|password|reveal|outsideTrustedNetworks|vault|Kancelář|false", "released|userName|copy|outsideTrustedNetworks|vault|Kancelář|false"], rows);
        Assert.Equal(2, WatchedBy(f, reader));
        // Two evaluations per release: the first step and the gate.
        Assert.Equal(6, policy.Evaluations);

        // Mail has no notes: nothing to release, audited as not_found.
        var empty = await Denied(404, () => Release(f, reader, mail, "notes"));
        Assert.Equal("secret_field_empty", empty.Code);
        Assert.Equal("not_found|notes|reveal", Assert.Single(await AuditRows(f, mail, brief: true), r => r.Contains("notes")));
        await Denied(400, () => Release(f, reader, router, "title"));
        await Denied(400, () => Release(f, reader, router, "password", "print"));
    }

    [PostgreSqlFact]
    public async Task Reauthentication_needs_a_proof_consumed_once_and_an_unsupported_sign_in_never_gets_a_value()
    {
        var policy = new FakeReleasePolicy { Decision = new ReleaseDecision("always", "secret", true, true, null) };
        await using var f = await VaultFixture.CreateAsync(release: policy);
        var (vault, router, _) = await AttachAsync(f);
        var reader = await f.Host.SeedUserAsync("reader");
        await Grant(f, vault, reader, "read");

        var (value, reauth) = await Release(f, reader, router);
        Assert.Null(value);
        Assert.Equal(new ReauthChallenge("always", "secret", true), reauth);
        Assert.Equal(0, policy.Consumptions);

        var proof = policy.IssueProof(reader, router, "reveal");
        Assert.Equal("r0uter-pw", (await Release(f, reader, router, proof: proof)).Values!.Values.Single().Value);
        // Replay: the proof is gone.
        Assert.Null((await Release(f, reader, router, proof: proof)).Values);
        // A proof for another operation or another account does not fit.
        Assert.Null((await Release(f, reader, router, "password", "copy", policy.IssueProof(reader, router, "reveal"))).Values);
        Assert.Null((await Release(f, reader, router, proof: policy.IssueProof(f.SpaceAdmin, router, "reveal"))).Values);
        Assert.Equal(["reauth_required|false", "released|true", "reauth_required|false", "reauth_required|false", "reauth_required|false"],
            await ScalarRows(f, router, "\"Result\" || '|' || \"ReauthUsed\""));

        // Unsupported: refused with supported false even with a proof, the proof is not consumed and no value is read.
        policy.Decision = new ReleaseDecision("always", "instance", true, false, null);
        var consumptions = policy.Consumptions;
        var unsupported = await Release(f, reader, router, proof: policy.IssueProof(reader, router, "reveal"));
        Assert.Null(unsupported.Values);
        Assert.Equal(new ReauthChallenge("always", "instance", false), unsupported.Reauth);
        Assert.Equal(consumptions, policy.Consumptions);
        Assert.Equal("unsupported|false", (await ScalarRows(f, router, "\"Result\" || '|' || \"ReauthUsed\""))[^1]);
    }

    [PostgreSqlFact]
    public async Task Several_fields_are_released_in_one_read_with_one_proof_and_one_audit_row_each()
    {
        var policy = new FakeReleasePolicy { Decision = new ReleaseDecision("always", "vault", true, true, null) };
        await using var f = await VaultFixture.CreateAsync(release: policy);
        var (vault, router, mail) = await AttachAsync(f);
        var reader = await f.Host.SeedUserAsync("reader");
        await Grant(f, vault, reader, "read");

        var (values, _) = await Release(f, reader, router, proof: policy.IssueProof(reader, router, "reveal"), fields: ["password", "userName", "password"]);
        Assert.Equal(new Dictionary<string, string> { ["userName"] = "admin", ["password"] = "r0uter-pw" }, values!.Values);
        Assert.Equal(1, policy.Consumptions);
        Assert.Equal(["released|userName|true", "released|password|true"], await ScalarRows(f, router, "\"Result\" || '|' || \"Field\" || '|' || \"ReauthUsed\""));

        // Empty fields are left out; the shorthand field works too.
        policy.Decision = new ReleaseDecision("never", "instance", false, true, null);
        Assert.Equal(["url"], (await Release(f, reader, mail, fields: ["notes", "url"])).Values!.Values.Keys);
        f.Host.Actor.Current = reader;
        using (var scope = f.Host.Services.CreateScope())
        {
            var single = await scope.ServiceProvider.GetRequiredService<SecretReleaseService>()
                .ReleaseAsync(mail, new ReleaseSecretRequest(null, "copy", null, "userName"), CancellationToken.None);
            Assert.Equal("atlas@example.test", single.Values!.Values["userName"]);
        }
        await Denied(400, () => Release(f, reader, router, fields: []));
    }

    [PostgreSqlFact]
    public async Task Fail_closed_policy_is_the_default_until_WP4_V4()
    {
        await using var f = await VaultFixture.CreateAsync();
        var (_, router, _) = await AttachAsync(f);
        var (value, reauth) = await Release(f, f.Admin, router);
        Assert.Null(value);
        Assert.Equal(new ReauthChallenge("always", "instance", false), reauth);
        Assert.Equal(["unsupported"], await ScalarRows(f, router, "\"Result\""));
    }

    [PostgreSqlFact]
    public async Task Denials_are_audited_and_answer_403_404_and_409()
    {
        var policy = new FakeReleasePolicy();
        await using var f = await VaultFixture.CreateAsync(release: policy);
        var (vault, router, mail) = await AttachAsync(f);
        var viewer = await f.Host.SeedUserAsync("viewer");
        var stranger = await f.Host.SeedUserAsync("stranger");
        var reader = await f.Host.SeedUserAsync("reader");
        await Grant(f, vault, viewer, "discover");
        await Grant(f, vault, reader, "read");

        await Denied(403, () => Release(f, viewer, router));
        await Denied(404, () => Release(f, stranger, router));
        // An unknown id has nothing to reference: 404 without a row.
        await Denied(404, () => Release(f, reader, Guid.NewGuid()));
        Assert.Equal(["forbidden", "not_found"], await ScalarRows(f, router, "\"Result\""));
        Assert.Equal(0, policy.Evaluations);

        // A removed secret is gone for everyone, WikiAdmin included.
        await f.Host.ExecuteAsync("UPDATE \"VaultSecret\" SET \"RemovedAt\" = now() WHERE \"Id\" = :id", mail);
        await Denied(404, () => Release(f, f.Admin, mail));
        Assert.Equal(["not_found"], await ScalarRows(f, mail, "\"Result\""));

        // Locked: no key and a stored password that no longer decrypts.
        f.Keyring.Lock(vault);
        await f.Host.ExecuteAsync("UPDATE \"Vault\" SET \"ProtectedKdbxPassword\" = 'CfDJ8-not-a-payload' WHERE \"Id\" = :id", vault);
        Assert.Equal("vault_locked", (await Denied(409, () => Release(f, reader, router))).Code);
        // Unavailable: the file is gone; no previous copy is served.
        await f.Operate(f.SpaceAdmin, o => o.ReplacePasswordAsync(vault, new VaultPasswordRequest(KdbxTestFiles.Password(Fixture)), CancellationToken.None));
        Assert.Equal("r0uter-pw", (await Release(f, reader, router)).Values!.Values.Single().Value);
        File.Delete(Path.Combine(f.Folder, "provoz.kdbx"));
        Assert.Equal("vault_unavailable", (await Denied(409, () => Release(f, reader, router))).Code);
        Assert.Equal(["forbidden", "not_found", "unavailable", "released", "unavailable"], await ScalarRows(f, router, "\"Result\""));
    }

    [PostgreSqlFact]
    public async Task No_committed_audit_row_means_no_value()
    {
        var policy = new FakeReleasePolicy();
        // Own database: the test adds a trigger to SecretReleaseEvent (DDL) and counts every value the host's watch holds.
        await using var f = await VaultFixture.CreateAsync(release: policy, isolated: true);
        var (_, router, _) = await AttachAsync(f);
        await f.Host.SeedAsync(session => session.CreateSQLQuery("""
            CREATE FUNCTION test_fail_release_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'audit storage unavailable'; END $$;
            CREATE TRIGGER "TR_Test_FailReleaseAudit" BEFORE INSERT ON "SecretReleaseEvent"
                FOR EACH ROW WHEN (NEW."Result" = 'released') EXECUTE FUNCTION test_fail_release_audit();
            """).ExecuteUpdateAsync());
        (SecretValuesView? Values, ReauthChallenge? Reauth) result = default;
        var error = await Record.ExceptionAsync(async () => result = await Release(f, f.Admin, router));
        Assert.NotNull(error);
        Assert.IsNotType<ApiException>(error);
        Assert.Null(result.Values);
        Assert.DoesNotContain("r0uter-pw", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(await ScalarRows(f, router, "\"Result\""));
        Assert.Equal(0, f.Host.Services.GetRequiredService<SecretReleaseWatch>().Watched);
    }

    [PostgreSqlFact]
    public async Task Releases_are_rate_limited_per_account()
    {
        var policy = new FakeReleasePolicy();
        await using var f = await VaultFixture.CreateAsync(release: policy,
            settings: new Dictionary<string, string?> { ["Vaults:Release:PerMinute"] = "2" });
        var (vault, router, _) = await AttachAsync(f);
        var reader = await f.Host.SeedUserAsync("reader");
        await Grant(f, vault, reader, "read");
        await Release(f, reader, router);
        await Release(f, reader, router);
        Assert.Equal("release_rate_limited", (await Denied(429, () => Release(f, reader, router))).Code);
        // Another account has its own window.
        Assert.NotNull((await Release(f, f.Admin, router)).Values);
        Assert.Equal(3, (await ScalarRows(f, router, "\"Result\"")).Count);
    }

    [PostgreSqlFact]
    public async Task Revoked_expired_and_archived_access_hides_released_values_live()
    {
        var policy = new FakeReleasePolicy();
        // Values stay shown for an hour here, so a slow run cannot let them lapse before the revocations under test.
        await using var f = await VaultFixture.CreateAsync(release: policy,
            settings: new Dictionary<string, string?> { ["Vaults:Release:ValueSeconds"] = "3600" });
        var events = f.Events;
        var (vault, router, mail) = await AttachAsync(f);
        var reader = await f.Host.SeedUserAsync("reader");
        var temporary = await f.Host.SeedUserAsync("temporary");
        var keeper = await f.Host.SeedUserAsync("keeper");
        await Grant(f, vault, reader, "read");
        await Grant(f, vault, keeper, "read");
        var expiry = DateTimeOffset.UtcNow.AddSeconds(20);
        await f.As(f.SpaceAdmin, new SetSecretGrant(mail, new UpdateVaultGrant(temporary.Id, null, "read", null, null, expiry)));
        await Release(f, temporary, mail);
        await Release(f, reader, router);
        await Release(f, keeper, router);
        // The host's events are every test's: this test's are those of its own secrets.
        Assert.DoesNotContain(events.Revocations, r => r.SecretId == router || r.SecretId == mail);

        // Revocation: after its commit, only the account that lost Čte is told; the next release is denied.
        await f.As(f.SpaceAdmin, new RevokeVaultGrant(vault, reader.Id, null, await VersionAsync(f, vault, reader)));
        Assert.Contains((reader.Id, router), await Eventually(events, () => events.Revocations.Contains((reader.Id, router))));
        Assert.DoesNotContain(events.Revocations, r => r.User == keeper.Id);
        await Denied(404, () => Release(f, reader, router));

        // Expiry has no commit: the periodic re-check finds it.
        var wait = expiry - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(200);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }
        await f.Host.Services.GetRequiredService<SecretReleaseWatch>().RecheckAsync(CancellationToken.None);
        Assert.Contains((temporary.Id, mail), events.Revocations);

        // A rejected request is not a deny: the keeper still reads, nobody is told.
        var asker = await f.Host.SeedUserAsync("asker");
        await Grant(f, vault, asker, "discover");
        var request = await f.As<AccessRequestView>(asker, new RequestSecretAccess(router, new CreateAccessRequest("read", "Servis routeru")));
        await f.As<AccessRequestView>(f.SpaceAdmin, new RejectAccessRequest(request.Id, "Ne"));
        Assert.DoesNotContain(events.Revocations, r => r.User == keeper.Id);

        // Archiving the vault hides every value still shown.
        await f.Operate(f.SpaceAdmin, o => o.ArchiveAsync(vault, CancellationToken.None));
        Assert.Contains((keeper.Id, router), await Eventually(events, () => events.Revocations.Contains((keeper.Id, router))));
        Assert.All(events.Payloads, payload => Assert.DoesNotContain("r0uter", payload, StringComparison.Ordinal));
    }

    [PostgreSqlFact]
    public async Task Vault_and_secret_audit_merge_releases_requests_and_grants_for_managers_only()
    {
        var policy = new FakeReleasePolicy();
        await using var f = await VaultFixture.CreateAsync(release: policy);
        var (vault, router, mail) = await AttachAsync(f);
        var reader = await f.Host.SeedUserAsync("reader");
        var asker = await f.Host.SeedUserAsync("asker");
        var secretManager = await f.Host.SeedUserAsync("secret-manager");
        await Grant(f, vault, reader, "read");
        await Grant(f, vault, asker, "discover");
        await f.As(f.SpaceAdmin, new SetSecretGrant(router, new UpdateVaultGrant(secretManager.Id, null, "manage", null)));
        await Release(f, reader, router);
        await Release(f, reader, mail, "userName");
        await Denied(403, () => Release(f, asker, router));
        var request = await f.As<AccessRequestView>(asker, new RequestSecretAccess(router, new CreateAccessRequest("read", "Servis")));
        await f.As<AccessRequestView>(f.SpaceAdmin, new ApproveAccessRequest(request.Id, null));

        var all = await Audit(f, f.SpaceAdmin, vault, null);
        Assert.Equal(["access_request.approved", "resource_grant.added", "access_request.created", "secret.release_denied", "secret.released",
            "secret.released", "resource_grant.added", "resource_grant.added", "resource_grant.added"], all.Items.Select(i => i.Action));
        var created = all.Items.Single(i => i.Action == "access_request.created");
        Assert.Equal(("Servis", asker.Id, "read", router), (created.Note, created.UserId!.Value, created.Role, created.SecretId!.Value));
        var released = all.Items.First(i => i.Action == "secret.released");
        Assert.Equal(("userName", "reveal", "released", "never", false), (released.Field, released.Operation, released.Result, released.Mode, released.ReauthUsed!.Value));
        Assert.Equal(vault, released.VaultId);

        // Filters: kind, result, actor, time and keyset paging.
        Assert.All((await Audit(f, f.SpaceAdmin, vault, null, kind: "release")).Items, i => Assert.StartsWith("secret.", i.Action));
        Assert.Equal(2, (await Audit(f, f.SpaceAdmin, vault, null, result: "released")).Items.Count);
        var byReader = (await Audit(f, f.SpaceAdmin, vault, null, actor: reader.Id)).Items;
        Assert.Equal(2, byReader.Count);
        Assert.All(byReader, i => Assert.Equal(reader.Id, i.ActorId));
        Assert.Empty((await Audit(f, f.SpaceAdmin, vault, null, from: DateTimeOffset.UtcNow.AddMinutes(1))).Items);
        var first = await Audit(f, f.SpaceAdmin, vault, null, limit: 4);
        Assert.True(first.HasMore);
        var second = await Audit(f, f.SpaceAdmin, vault, null, limit: 100, before: first.NextBeforeId);
        Assert.Equal(all.Items.Skip(4).Select(i => i.Id), second.Items.Select(i => i.Id));

        // The secret audit is that secret only; its manager reads it but not the vault audit.
        var secretAudit = await Audit(f, secretManager, null, router);
        Assert.All(secretAudit.Items, i => Assert.Equal(router, i.SecretId));
        Assert.DoesNotContain(secretAudit.Items, i => i.Field == "userName");
        await Denied(403, () => Audit(f, reader, vault, null));
        await Denied(403, () => Audit(f, secretManager, vault, null));
        var nobody = await f.Host.SeedUserAsync("nobody");
        await Denied(404, () => Audit(f, nobody, vault, null));
        await Denied(400, () => Audit(f, f.SpaceAdmin, vault, null, kind: "everything"));
        Assert.NotEmpty((await Audit(f, f.Admin, vault, null)).Items);

        // The global access audit (WikiAdmin) carries the release events too.
        var global = await f.As<AccessAuditList>(f.Admin, new GetAccessAudit(null, 100, UserId: reader.Id));
        Assert.Contains(global.Items, i => i.Action == "secret.released" && i.SecretId == router && i.Field == "password");
    }

    internal static async Task<(Guid Vault, Guid Router, Guid Mail)> AttachAsync(VaultFixture f)
    {
        var location = await f.LocalLocationAsync();
        var vault = await f.AttachAsync(location.Id, "provoz.kdbx", Fixture);
        var secrets = await f.As<IReadOnlyList<VaultSecretView>>(f.SpaceAdmin, new ListVaultSecrets(vault.Id));
        return (vault.Id, secrets.Single(s => s.Title == "Router").SecretId, secrets.Single(s => s.Title == "Mail").SecretId);
    }

    internal static Task Grant(VaultFixture f, Guid vault, UserAccount subject, string level) =>
        f.As(f.SpaceAdmin, new SetVaultGrant(vault, new UpdateVaultGrant(subject.Id, null, level, null)));

    /// <summary>The events once <paramref name="done"/> holds; the post-commit re-check is best effort within the response budget.</summary>
    private static async Task<IReadOnlyList<(Guid User, Guid SecretId)>> Eventually(RecordingSecurityEvents events, Func<bool> done)
    {
        for (var attempt = 0; attempt < 100 && !done(); attempt++)
        {
            await Task.Delay(100);
        }
        return events.Revocations;
    }

    internal static async Task<(SecretValuesView? Values, ReauthChallenge? Reauth)> Release(VaultFixture f, UserAccount actor, Guid secret,
        string field = "password", string operation = "reveal", Guid? proof = null, string[]? fields = null)
    {
        f.Host.Actor.Current = actor;
        using var scope = f.Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SecretReleaseService>()
            .ReleaseAsync(secret, new ReleaseSecretRequest(fields ?? [field], operation, proof), CancellationToken.None);
    }

    /// <summary>The values the account holds in the host's watch (SecretReleaseWatch.Watched counts every test of a shared host).</summary>
    private static int WatchedBy(VaultFixture f, UserAccount user) =>
        ((System.Collections.Concurrent.ConcurrentDictionary<(Guid UserId, Guid SecretId), DateTime>)typeof(SecretReleaseWatch)
            .GetField("m_released", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(f.Host.Services.GetRequiredService<SecretReleaseWatch>())!).Keys.Count(key => key.UserId == user.Id);

    internal static async Task<ApiException> Denied(int status, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(status, error.Status);
        return error;
    }

    private static Task<VaultAuditList> Audit(VaultFixture f, UserAccount caller, Guid? vault, Guid? secret, string? kind = null, string? result = null,
        Guid? actor = null, DateTimeOffset? from = null, int limit = 100, string? before = null) =>
        f.As<VaultAuditList>(caller, new GetVaultAudit(vault, secret, actor, kind, result, from, null, before, limit));

    private static async Task<IReadOnlyList<string>> AuditRows(VaultFixture f, Guid secret, bool brief = false) =>
        await ScalarRows(f, secret, brief
            ? "\"Result\" || '|' || \"Field\" || '|' || \"Operation\""
            : "\"Result\" || '|' || \"Field\" || '|' || \"Operation\" || '|' || \"Mode\" || '|' || \"ModeSource\" || '|' || COALESCE(\"MatchedNetwork\",'') || '|' || \"ReauthUsed\"");

    private static async Task<IReadOnlyList<string>> ScalarRows(VaultFixture f, Guid secret, string expression)
    {
        using var session = f.Host.OpenSession();
        return (await session.CreateSQLQuery($"SELECT CAST(({expression}) AS text) FROM \"SecretReleaseEvent\" WHERE \"SecretId\" = :id ORDER BY \"Id\"")
            .SetParameter("id", secret).ListAsync<string>()).ToArray();
    }

    private static async Task<string?> VersionAsync(VaultFixture f, Guid vault, UserAccount user)
    {
        var state = await f.As<VaultAccessState>(f.SpaceAdmin, new GetVaultAccess(vault));
        return state.People.SingleOrDefault(p => p.UserId == user.Id)?.Sources.SingleOrDefault(s => s.GroupId is null && s.Kind == "vault")?.Version;
    }
}
