using System.Data;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.ModuleSettings.Domain;

namespace Atlas.Server.Features.ModuleSettings;

public static class OidcRuntimeLoader
{
    /// <summary>Documented SQL exception (spec §8): this runs before WebApplication.Build(), so there is no session factory
    /// and no container yet. Deliberately static for the same reason; the logger is optional because the tests call it directly.</summary>
    public static async Task<OidcRuntimeConfiguration> LoadAsync(IConfiguration configuration, IHostEnvironment environment, ILogger? logger = null, CancellationToken ct = default)
    {
        var options = AtlasOidcOptions.Read(configuration, validate: false);
        var connectionString = configuration.GetConnectionString("atlas-db");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return Validated(null, options);
        }
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await using var exists = new NpgsqlCommand("SELECT to_regclass('\"OidcSettingsRevision\"') IS NOT NULL", connection, tx);
            if (!(bool)(await exists.ExecuteScalarAsync(ct))!)
            {
                return Validated(null, options);
            }
            Guid version;
            await using (var head = new NpgsqlCommand("SELECT \"Id\",\"PublicOrigin\" FROM \"OidcSettingsRevision\" WHERE \"ActiveTo\" IS NULL", connection, tx))
            await using (var reader = await head.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                {
                    return Validated(null, options);
                }
                version = reader.GetGuid(0);
                options.PublicOrigin = reader.GetString(1);
            }
            var keys = Path.GetFullPath(configuration["DataProtection:KeysPath"] ?? Path.Combine(environment.ContentRootPath, ".data", "keys"));
            PrivateServerFiles.PrepareDirectory(keys, environment);
            var protector = DataProtectionProvider.Create(new DirectoryInfo(keys), builder => builder.SetApplicationName("ScaleAtlas"))
                .CreateProtector(OidcProtection.Purpose);
            var providers = new List<AtlasOidcProvider>();
            await using (var command = new NpgsqlCommand("SELECT \"ProviderId\",\"DisplayName\",\"Issuer\",\"ClientId\",\"ProtectedSecret\",\"AllowSignup\" FROM \"OidcProviderSettings\" WHERE \"SettingsId\"=@version ORDER BY \"ProviderId\"", connection, tx))
            {
                command.Parameters.AddWithValue("version", version);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    providers.Add(new AtlasOidcProvider
                    {
                        Id = reader.GetString(0),
                        DisplayName = reader.GetString(1),
                        Issuer = reader.GetString(2),
                        ClientId = reader.GetString(3),
                        ClientSecret = reader.IsDBNull(4) ? null : protector.Unprotect(reader.GetString(4)),
                        AllowSignup = reader.GetBoolean(5),
                        Scopes = []
                    });
                }
            }
            await using (var command = new NpgsqlCommand("SELECT \"ProviderId\",\"Scope\" FROM \"OidcProviderScope\" WHERE \"SettingsId\"=@version ORDER BY \"Scope\"", connection, tx))
            {
                command.Parameters.AddWithValue("version", version);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var provider = providers.Single(p => p.Id == reader.GetString(0));
                    provider.Scopes = [.. provider.Scopes, reader.GetString(1)];
                }
            }
            // Startup precedes migrations: an older installation has no mapping table yet.
            await using (var mappingTable = new NpgsqlCommand("SELECT to_regclass('\"OidcAdminMapping\"') IS NOT NULL", connection, tx))
            {
                if ((bool)(await mappingTable.ExecuteScalarAsync(ct))!)
                {
                    await using var command = new NpgsqlCommand("SELECT \"ProviderId\",\"ClaimType\",\"ClaimValue\" FROM \"OidcAdminMapping\" WHERE \"SettingsId\"=@version ORDER BY \"ClaimType\",\"ClaimValue\"", connection, tx);
                    command.Parameters.AddWithValue("version", version);
                    await using var reader = await command.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var provider = providers.Single(p => p.Id == reader.GetString(0));
                        provider.AdminMappings = [.. provider.AdminMappings, new(reader.GetString(1), reader.GetString(2))];
                    }
                }
            }
            options.Providers = providers.ToArray();
            await tx.CommitAsync(ct);
            return Validated(version, options);
        }
        catch (NpgsqlException ex)
        {
            if (configuration.GetValue<bool>("Authentication:OpenIdConnect:RequireDatabaseAtStartup"))
            {
                logger?.LogError("Stored OIDC settings are unavailable ({ErrorType}); refusing to start without saved login providers.", ex.GetType().Name);
                throw;
            }
            // Database availability belongs to the retrying AtlasDatabaseInitializer and /health/ready, not to
            // the boot sequence: a wiki whose database is down must still serve /api/setup/status over HTTP.
            // Options are re-read so a half-applied revision (PublicOrigin without its providers) cannot leak.
            logger?.LogWarning("Stored OIDC settings are unavailable ({ErrorType}); starting with configured providers only.", ex.GetType().Name);
            return new OidcRuntimeConfiguration(null, AtlasOidcOptions.Read(configuration, validate: false));
        }
        catch (CryptographicException)
        {
            // A lost Data Protection keyring is a misconfiguration, not an outage: starting would silently
            // drop every saved provider. Do not expose provider secrets or protected payloads.
            throw new InvalidOperationException("Nelze načíst uložené nastavení OIDC. Ověřte databázi a obnovení původních Data Protection klíčů.");
        }
    }

    private static OidcRuntimeConfiguration Validated(Guid? version, AtlasOidcOptions options)
    {
        options.Validate();
        return new OidcRuntimeConfiguration(version, options);
    }
}
