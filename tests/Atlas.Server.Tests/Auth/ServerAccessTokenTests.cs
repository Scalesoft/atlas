using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NHibernate.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;
using Atlas.Server.Features.Auth;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Auth.Recovery;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Features.Setup.Commands;
using Atlas.Server.Features.Auth.Builtin;

namespace Atlas.Server.Tests.Auth;

public sealed class ServerAccessTokenTests
{
    [Fact]
    public async Task Token_files_have_restricted_permissions_and_survive_a_new_file_service()
    {
        var env = new TokenEnvironment();
        try
        {
            var config = new ConfigurationBuilder().Build();
            var files = new ServerTokenFiles(config, env);
            var token = await files.BootstrapAsync(null, default);
            Assert.Equal(token, await new ServerTokenFiles(config, env).BootstrapAsync(null, default));
            if (OperatingSystem.IsWindows())
            {
                var acl = new FileInfo(files.BootstrapPath).GetAccessControl();
                Assert.True(acl.AreAccessRulesProtected);
                var allowed = new HashSet<string> { WindowsIdentity.GetCurrent().User!.Value, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value };
                Assert.All(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule => { if (OperatingSystem.IsWindows()) Assert.Contains(rule.IdentityReference.Value, allowed); });
            }
            else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files.BootstrapPath));
        }
        finally { DeleteOwnDirectory(env.ContentRootPath); }
    }
    [Fact]
    public async Task Public_web_directory_is_never_allowed_to_store_server_capabilities()
    {
        var environment = new TokenEnvironment();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Security:DataPath"] = Path.Combine(environment.ContentRootPath,"wwwroot","security") }).Build();
        var files = new ServerTokenFiles(config, environment);
        await Assert.ThrowsAsync<ApiException>(() => files.BootstrapAsync(null, default));
        Assert.False(Directory.Exists(files.DirectoryPath));
        try
        {
            // A restored existing file must not bypass the same public-directory guard.
            Directory.CreateDirectory(files.DirectoryPath);
            await File.WriteAllTextAsync(files.RecoveryPath, "{}");
            await Assert.ThrowsAsync<ApiException>(() => files.ReadRecoveryAsync(default));
        }
        finally { DeleteOwnDirectory(environment.ContentRootPath); }
    }
    [PostgreSqlFact]
    public async Task Bootstrap_persists_logs_once_and_never_logs_after_setup_or_restart()
    {
        await using var f = await Fixture.Create();
        var tokens = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Tokens.EnsureBootstrapAsync()));
        Assert.True(tokens.All(t => t == tokens[0] && t?.Length == 64));
        Assert.Equal(1, f.Log.Count);
        Assert.Equal(tokens[0], (await File.ReadAllTextAsync(f.Files.BootstrapPath)).Trim());
        await f.Setup(tokens[0]!);
        Assert.Null(await f.Tokens.EnsureBootstrapAsync());
        var restartedLog = new TokenLogger(); var restarted = f.NewService(restartedLog);
        Assert.Null(await restarted.EnsureBootstrapAsync()); Assert.Equal(0, restartedLog.Count);
        await Assert.ThrowsAsync<ApiException>(() => restarted.ValidateBootstrapAsync(tokens[0], default));
    }

    [PostgreSqlFact]
    public async Task Configured_bootstrap_is_materialized_and_recovery_rotates_once_under_concurrency()
    {
        await using var f = await Fixture.Create(new Dictionary<string, string?> { ["Setup:BootstrapToken"] = new string('s', 40) });
        Assert.Equal(new string('s', 40), await f.Tokens.EnsureBootstrapAsync());
        var admin = await f.Setup(new string('s', 40));
        var originalStamp = admin.SecurityStamp;
        using (var seedSession = f.Host.OpenSession())
        {
            using var tx = seedSession.BeginTransaction();
            var seeded = (await seedSession.GetAsync<UserAccount>(admin.Id))!;
            seeded.OidcAdminIssuer = "https://hub.example/";
            await AccountHistory.RecordAsync(seedSession, seeded, null, CancellationToken.None);
            await tx.CommitAsync();
        }
        var original = await f.Tokens.EnsureRecoveryAsync(); Assert.NotNull(original);
        Assert.Equal(admin.Id, original.UserId); Assert.Equal(DateTimeKind.Utc, original.ExpiresAt.Kind);
        Assert.True(original.ExpiresAt > DateTime.UtcNow.AddDays(6));
        var before = f.Log.Count;
        // SEC-A-7: the log names the file, never the token in it.
        Assert.Contains(f.Log.Messages, message => message.Contains(f.Files.RecoveryPath, StringComparison.Ordinal));
        Assert.DoesNotContain(f.Log.Messages, message => message.Contains(original.Token, StringComparison.Ordinal));
        var status = await f.Tokens.GetRecoveryStatusAsync(); Assert.Equal(before, f.Log.Count);
        // SEC-A-9: the anonymous status names the file, not the server's directories.
        Assert.Equal("administrator-recovery.json", status.TokenFilePath);
        Assert.DoesNotContain(f.Files.DirectoryPath, status.Instructions, StringComparison.Ordinal);
        async Task<bool> Attempt()
        {
            try { await f.Tokens.RecoverAsync(new(original.Token, "replacement password 123"), default); return true; }
            catch (ApiException e) when (e.Status == 403) { return false; }
        }
        var attempts = await Task.WhenAll(Attempt(), Attempt()); Assert.Single(attempts, success => success);
        var rotated = await f.Files.ReadRecoveryAsync(default); Assert.NotNull(rotated); Assert.NotEqual(original.Token, rotated.Token);
        using var session = f.Host.OpenSession();
        var current = await session.GetAsync<UserAccount>(admin.Id); Assert.True(current.IsActive && current.IsWikiAdmin); Assert.NotEqual(originalStamp, current.SecurityStamp);
        Assert.Single(await session.Query<UserAccount>().ToListAsync());
        Assert.Null(current.OidcAdminIssuer);
        var credential = await session.GetAsync<BuiltinCredential>(admin.Id);
        Assert.NotEqual(PasswordVerificationResult.Failed, new PasswordHasher<UserAccount>().VerifyHashedPassword(current, credential.PasswordHash!, "replacement password 123"));
        Assert.Equal(1, await session.CreateSQLQuery("SELECT COUNT(*) FROM \"ServerRecoveryToken\" WHERE \"ConsumedAt\" IS NULL").UniqueResultAsync<long>());
        Assert.Equal(1, await session.CreateSQLQuery("SELECT COUNT(*) FROM \"ServerRecoveryToken\" WHERE \"ConsumedAt\" IS NOT NULL").UniqueResultAsync<long>());
    }

    [PostgreSqlFact]
    public async Task Recovery_restores_demoted_inactive_target_with_builtin_disabled_and_reconciles_expired_file()
    {
        await using var f = await Fixture.Create(); var admin = await f.Setup((await f.Tokens.EnsureBootstrapAsync())!);
        var original = (await f.Tokens.EnsureRecoveryAsync())!;
        using (var s = f.Host.OpenSession())
        {
            using var tx = s.BeginTransaction();
            await s.CreateSQLQuery("UPDATE \"ServerRecoveryToken\" SET \"IssuedAt\"=now()-interval '8 days',\"ExpiresAt\"=now()-interval '1 day'").ExecuteUpdateAsync();
            await tx.CommitAsync();
        }
        var fresh = (await f.Tokens.EnsureRecoveryAsync())!; Assert.NotEqual(original.Token, fresh.Token);
        await Assert.ThrowsAsync<ApiException>(() => f.Tokens.RecoverAsync(new(original.Token, "replacement password"), default));
        f.Recovery.Username = "nonexistent-admin";
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Tokens.RecoverAsync(new(fresh.Token, "replacement password"), default))).Status);
        f.Recovery.Username = null;
        var disabled = f.NewService(new TokenLogger(), builtinEnabled: false);
        Assert.True((await disabled.GetRecoveryStatusAsync()).Available);
        Assert.False((await disabled.GetRecoveryStatusAsync()).BuiltinEnabled);
        using (var s = f.Host.OpenSession())
        {
            using var tx = s.BeginTransaction(); var target = await s.GetAsync<UserAccount>(admin.Id); target.IsWikiAdmin = false; target.IsActive = false; await AccountHistory.RecordAsync(s, target, null, CancellationToken.None); await tx.CommitAsync();
        }
        Assert.NotNull(await f.Tokens.EnsureRecoveryAsync());
        var restored = await disabled.RecoverAsync(new(fresh.Token, "replacement password"), default);
        Assert.Equal(admin.Username, restored.Username);
        using var check = f.Host.OpenSession();
        var restoredAccount = await check.GetAsync<UserAccount>(admin.Id);
        Assert.True(restoredAccount.IsWikiAdmin);
        Assert.True(restoredAccount.IsActive);
        Assert.NotEqual(admin.SecurityStamp, restoredAccount.SecurityStamp);
        Assert.Null(restoredAccount.OidcAdminIssuer);
    }

    [PostgreSqlFact]
    public async Task Server_selected_existing_account_can_be_recovered_without_any_current_administrator()
    {
        await using var f = await Fixture.Create();
        var admin = await f.Setup((await f.Tokens.EnsureBootstrapAsync())!);
        using (var session = f.Host.OpenSession())
        {
            using var tx = session.BeginTransaction();
            var account = await session.GetAsync<UserAccount>(admin.Id);
            account.IsWikiAdmin = false;
            await AccountHistory.RecordAsync(session, account, null, CancellationToken.None);
            await tx.CommitAsync();
        }
        f.Recovery.Username = admin.Username;
        var token = await f.Tokens.EnsureRecoveryAsync();
        Assert.NotNull(token);
        await f.Tokens.RecoverAsync(new(token.Token, "replacement password"), default);
        using var check = f.Host.OpenSession();
        Assert.True((await check.GetAsync<UserAccount>(admin.Id)).IsWikiAdmin);
    }

    private static void DeleteOwnDirectory(string path)
    {
        var resolved = Path.GetFullPath(path);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("atlas-token-test-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unexpected test directory.");
        }
        if (Directory.Exists(resolved))
        {
            Directory.Delete(resolved, true);
        }
    }
    private sealed class TokenEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ServerTokenTests";
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "atlas-token-test-" + Guid.NewGuid().ToString("N"));
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class TokenLogger : ILogger<ServerAccessTokens>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public int Count => Messages.Count;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) => Messages.Enqueue(format(state, error));
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TokenEnvironment m_environment;
        private readonly IConfiguration m_configuration;
        private readonly AtlasTestHost m_host;

        private Fixture(TokenEnvironment environment, IConfiguration configuration, ServerTokenFiles files, TokenLogger log, AtlasTestHost host)
        {
            m_environment = environment;
            m_configuration = configuration;
            m_host = host;
            Files = files;
            Log = log;
        }

        public AtlasTestHost Host => m_host;
        public ServerTokenFiles Files { get; }
        public TokenLogger Log { get; }
        public ServerAccessTokens Tokens => m_host.Services.GetRequiredService<ServerAccessTokens>();
        public RecoveryOptions Recovery => m_host.Services.GetRequiredService<RecoveryOptions>();

        public static async Task<Fixture> Create(Dictionary<string, string?>? overrides = null)
        {
            var environment = new TokenEnvironment();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(overrides ?? []).Build();
            var files = new ServerTokenFiles(configuration, environment);
            var log = new TokenLogger();
            // Own database: the first start of an empty installation (bootstrap token, CompleteSetup) and its one current recovery
            // token (UX_ServerRecoveryToken_Current); the recovery picks the account among all of them.
            var host = await AtlasTestHost.CreateIsolatedAsync("atlas_recovery_", services =>
            {
                services.AddAuth();
                services.AddSingleton(files);
                services.AddSingleton(provider => new ServerAccessTokens(
                    provider.GetRequiredService<IServiceScopeFactory>(), files, configuration, log,
                    new BuiltinAuthenticationOptions(true), BuiltinPasswordPolicy.Production));
            });
            return new Fixture(environment, configuration, files, log, host);
        }

        public ServerAccessTokens NewService(TokenLogger log, bool builtinEnabled = true)
            => new(m_host.Services.GetRequiredService<IServiceScopeFactory>(), Files, m_configuration, log, new BuiltinAuthenticationOptions(builtinEnabled), BuiltinPasswordPolicy.Production);

        public Task<UserAccount> Setup(string token)
            => m_host.InvokeAsync<UserAccount>(new CompleteSetup(token, "Recovery test", "admin", "Administrator", "initial password 123"));

        public async ValueTask DisposeAsync()
        {
            await m_host.DisposeAsync();
            // This directory is generated by this fixture under the OS temp folder only.
            DeleteOwnDirectory(m_environment.ContentRootPath);
        }
    }
}
