using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Features.ModuleSettings.Contracts;
using Atlas.Server.Features.ModuleSettings.Domain;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class OidcRuntimeTests
{
    [Fact]
    public async Task Automatic_certificates_persist_distinct_keys_across_concurrent_startup_and_rotate_with_overlap()
    {
        using var storage = new AttachmentTests.Storage();
        var env = new AttachmentTests.EnvironmentStub(storage.Root) { EnvironmentName = "Production" };
        var config = new ConfigurationBuilder().Build();
        var keys = await Task.WhenAll(Enumerable.Range(0,2).Select(_ => Task.Run(() => OidcCertificates.Load(config, env, new()))));
        try
        {
            var signing = Assert.Single(keys[0].Signing); var encryption = Assert.Single(keys[0].Encryption);
            Assert.NotEqual(signing.Thumbprint, encryption.Thumbprint);
            Assert.Equal(signing.Thumbprint, Assert.Single(keys[1].Signing).Thumbprint);
            Assert.Equal(encryption.Thumbprint, Assert.Single(keys[1].Encryption).Thumbprint);
            using var privateKey = signing.GetRSAPrivateKey()!; using var publicKey = keys[1].Signing[0].GetRSAPublicKey()!;
            var payload = "state continuity"u8.ToArray();
            Assert.True(publicKey.VerifyData(payload, privateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var recipient = encryption.GetRSAPublicKey()!; using var decryptor = keys[1].Encryption[0].GetRSAPrivateKey()!;
            Assert.Equal(payload, decryptor.Decrypt(recipient.Encrypt(payload, RSAEncryptionPadding.OaepSHA256), RSAEncryptionPadding.OaepSHA256));
            using var renewed = OidcCertificates.Load(config, env, new(), new DateTimeOffset(signing.NotAfter.ToUniversalTime()).AddDays(-20));
            Assert.Equal(2, renewed.Signing.Count); Assert.Equal(2, renewed.Encryption.Count);
            Assert.Contains(renewed.Signing, cert => cert.Thumbprint == signing.Thumbprint);
            Assert.Contains(renewed.Signing, cert => cert.NotAfter > signing.NotAfter);
        }
        finally { foreach (var key in keys) key.Dispose(); }
    }
    [Fact]
    public void Explicit_certificate_paths_take_precedence_and_partial_configuration_is_rejected()
    {
        using var storage = new AttachmentTests.Storage();
        var env = new AttachmentTests.EnvironmentStub(storage.Root);
        var config = new ConfigurationBuilder().Build();
        using var generated = OidcCertificates.Load(config, env, new());
        var directory = Path.Combine(storage.Root,".data","oidc-certificates");
        var signing = Assert.Single(Directory.GetFiles(directory,"signing-*.pfx"));
        var encryption = Assert.Single(Directory.GetFiles(directory,"encryption-*.pfx"));
        using var explicitKeys = OidcCertificates.Load(config,env,new() { SigningCertificatePath=signing, EncryptionCertificatePath=encryption });
        Assert.Equal(generated.Signing[0].Thumbprint,Assert.Single(explicitKeys.Signing).Thumbprint);
        Assert.Equal(generated.Encryption[0].Thumbprint,Assert.Single(explicitKeys.Encryption).Thumbprint);
        Assert.Throws<InvalidOperationException>(() => OidcCertificates.Load(config,env,new() { SigningCertificatePath=signing }));
    }
    [Fact]
    public void Automatic_certificate_storage_rejects_wwwroot_and_corrupt_existing_key_without_replacing_it()
    {
        using var storage = new AttachmentTests.Storage();
        var env = new AttachmentTests.EnvironmentStub(storage.Root);
        var publicConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Authentication:OpenIdConnect:CertificateDirectory"] = Path.Combine(storage.Root,"wwwroot","oidc") }).Build();
        Assert.Throws<InvalidOperationException>(() => OidcCertificates.Load(publicConfig, env, new()));
        var directory = Path.Combine(storage.Root,".data","oidc-certificates"); Directory.CreateDirectory(directory);
        var key = Path.Combine(directory,"signing-corrupted.pfx"); File.WriteAllText(key,"corrupt-key-sentinel");
        Assert.Throws<CryptographicException>(() => OidcCertificates.Load(new ConfigurationBuilder().Build(),env,new()));
        Assert.Equal("corrupt-key-sentinel", File.ReadAllText(key));
        Assert.Single(Directory.GetFiles(directory,"*.pfx"));
    }
    [PostgreSqlFact]
    public async Task Persisted_providers_override_configuration_before_validation_and_secret_uses_original_DP_keyring()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var config = fixture.Config(builtin: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => OidcRuntimeLoader.LoadAsync(config, fixture.Environment));
        await fixture.CreateSettingsTables();
        var protector = DataProtectionProvider.Create(new DirectoryInfo(fixture.KeysPath), b => b.SetApplicationName("ScaleAtlas"))
            .CreateProtector("ScaleAtlas.Modules.Oidc.ClientSecret.v1");
        var version = Guid.NewGuid();
        await fixture.Execute("INSERT INTO \"OidcSettingsRevision\" VALUES (@id,'https://wiki.example',NULL); INSERT INTO \"OidcProviderSettings\" VALUES (@id,'hub','Company Hub','https://idp.example','client',@secret,false); INSERT INTO \"OidcProviderScope\" VALUES (@id,'hub','openid')", version, protector.Protect("test-client-secret"));
        var loaded = await OidcRuntimeLoader.LoadAsync(config, fixture.Environment);
        Assert.Equal(version, loaded.Version); Assert.False(loaded.Options.BuiltinEnabled);
        Assert.Equal("https://wiki.example", loaded.Options.PublicOrigin);
        var provider = Assert.Single(loaded.Options.Providers);
        Assert.Equal("test-client-secret", provider.ClientSecret); Assert.Equal(["openid"], provider.Scopes);
        await fixture.Execute("CREATE TABLE \"OidcAdminMapping\"(\"SettingsId\" uuid,\"ProviderId\" text,\"ClaimType\" text,\"ClaimValue\" text); INSERT INTO \"OidcAdminMapping\" VALUES (@id,'hub','groups','atlas-admins')", version, null);
        var mapped = await OidcRuntimeLoader.LoadAsync(config, fixture.Environment);
        Assert.Equal(new OidcAdminMapping("groups", "atlas-admins"), Assert.Single(Assert.Single(mapped.Options.Providers).AdminMappings));
        await fixture.Execute("UPDATE \"OidcProviderSettings\" SET \"ProtectedSecret\"=@secret", version, "invalid-protected-sentinel");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => OidcRuntimeLoader.LoadAsync(config, fixture.Environment));
        Assert.DoesNotContain("invalid-protected-sentinel", failure.ToString());
        Assert.Null(failure.InnerException);
    }
    [PostgreSqlFact]
    public async Task Missing_table_or_current_row_uses_configuration_but_saved_empty_list_replaces_configuration()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var config = fixture.Config(builtin: true, serverProvider: true);
        Assert.Equal("configured", Assert.Single((await OidcRuntimeLoader.LoadAsync(config,fixture.Environment)).Options.Providers).Id);
        await fixture.CreateSettingsTables();
        Assert.Null((await OidcRuntimeLoader.LoadAsync(config,fixture.Environment)).Version);
        var id = Guid.NewGuid(); await fixture.Execute("INSERT INTO \"OidcSettingsRevision\" VALUES (@id,'',NULL)",id,null);
        var empty = await OidcRuntimeLoader.LoadAsync(config,fixture.Environment);
        Assert.Equal(id,empty.Version); Assert.Empty(empty.Options.Providers);
    }
    [Fact]
    public async Task An_unreachable_database_starts_with_configured_providers_and_a_warning()
    {
        using var storage = new AttachmentTests.Storage();
        var logs = new Tests.Platform.CaptureLoggerProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:atlas-db"] = "Host=127.0.0.1;Port=1;Database=atlas;Username=postgres;Timeout=1",
            ["Authentication:Builtin:Enabled"] = "true",
            ["DataProtection:KeysPath"] = Path.Combine(storage.Root, "keys")
        }).Build();
        var loaded = await OidcRuntimeLoader.LoadAsync(config, new AttachmentTests.EnvironmentStub(storage.Root), logs.CreateLogger("loader"));
        Assert.Null(loaded.Version);
        Assert.Empty(loaded.Options.Providers);
        Assert.Contains(logs.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }
    [Fact]
    public async Task Required_startup_database_failure_does_not_silently_drop_saved_login_providers()
    {
        using var storage = new AttachmentTests.Storage();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:atlas-db"] = "Host=127.0.0.1;Port=1;Database=atlas;Username=postgres;Timeout=1",
            ["Authentication:OpenIdConnect:RequireDatabaseAtStartup"] = "true"
        }).Build();

        await Assert.ThrowsAsync<NpgsqlException>(() => OidcRuntimeLoader.LoadAsync(config,
            new AttachmentTests.EnvironmentStub(storage.Root)));
    }

    private sealed class LoaderFixture : IAsyncDisposable
    {
        private readonly string schema = "atlas_oidc_loader_" + Guid.NewGuid().ToString("N");
        private readonly AttachmentTests.Storage storage = new();
        private NpgsqlConnection connection = null!;
        private string connectionString = "";
        public string KeysPath => Path.Combine(storage.Root,"keys");
        public AttachmentTests.EnvironmentStub Environment => new(storage.Root);
        public IConfiguration Config(bool builtin, bool serverProvider = false)
        {
            var values = new Dictionary<string,string?> { ["ConnectionStrings:atlas-db"] = connectionString, ["DataProtection:KeysPath"] = KeysPath, ["Authentication:Builtin:Enabled"] = builtin.ToString() };
            if (serverProvider) { values["Authentication:OpenIdConnect:PublicOrigin"]="https://configured.example"; values["Authentication:OpenIdConnect:Providers:0:Id"]="configured"; values["Authentication:OpenIdConnect:Providers:0:DisplayName"]="Configured"; values["Authentication:OpenIdConnect:Providers:0:Issuer"]="https://configured-idp.example"; values["Authentication:OpenIdConnect:Providers:0:ClientId"]="configured-client"; }
            return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        }
        public static async Task<LoaderFixture> CreateAsync()
        {
            var f = new LoaderFixture(); var cs = new NpgsqlConnectionStringBuilder(PostgreSqlTestConnection.Read("ATLAS_TEST_CONNECTION"));
            f.connection = new(cs.ConnectionString); await f.connection.OpenAsync();
            await using (var command = new NpgsqlCommand($"CREATE SCHEMA {f.schema}",f.connection)) await command.ExecuteNonQueryAsync();
            cs.SearchPath=f.schema+",public"; f.connectionString=cs.ConnectionString;
            await using (var command = new NpgsqlCommand($"SET search_path TO {f.schema},public",f.connection)) await command.ExecuteNonQueryAsync();
            Directory.CreateDirectory(f.KeysPath); return f;
        }
        public async Task CreateSettingsTables()
        {
            await using var command = new NpgsqlCommand("CREATE TABLE \"OidcSettingsRevision\"(\"Id\" uuid,\"PublicOrigin\" text,\"ActiveTo\" timestamptz); CREATE TABLE \"OidcProviderSettings\"(\"SettingsId\" uuid,\"ProviderId\" text,\"DisplayName\" text,\"Issuer\" text,\"ClientId\" text,\"ProtectedSecret\" text,\"AllowSignup\" boolean); CREATE TABLE \"OidcProviderScope\"(\"SettingsId\" uuid,\"ProviderId\" text,\"Scope\" text)",connection);
            await command.ExecuteNonQueryAsync();
        }
        public async Task Execute(string sql,Guid id,string? secret)
        {
            await using var command = new NpgsqlCommand(sql,connection); command.Parameters.AddWithValue("id",id); command.Parameters.Add("secret",NpgsqlTypes.NpgsqlDbType.Text).Value=(object?)secret??DBNull.Value; await command.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE",connection); await command.ExecuteNonQueryAsync();
            await connection.DisposeAsync(); storage.Dispose();
        }
    }
}
