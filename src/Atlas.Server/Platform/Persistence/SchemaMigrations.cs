using FluentMigrator;

namespace Atlas.Server.Platform.Persistence;

/// <summary>SQL remains embedded and immutable; FluentMigrator owns execution and the journal.</summary>
public abstract class EmbeddedSchemaMigration(string resourceName) : Migration
{
    public override void Up() => Execute.EmbeddedScript(resourceName);

    public override void Down()
        => throw new NotSupportedException("Automatic downgrade is not supported. Restore a verified backup instead.");
}

[Migration(1, "Initial wiki schema")]
public sealed class InitialSchemaMigration() : EmbeddedSchemaMigration("001_initial.sql");

[Migration(2, "Published search projection")]
public sealed class SearchSchemaMigration() : EmbeddedSchemaMigration("002_search.sql");

[Migration(3, "Appearance history")]
public sealed class AppearanceHistoryMigration() : EmbeddedSchemaMigration("003_appearance_history.sql");

[Migration(4, "Publication history")]
public sealed class PublicationHistoryMigration() : EmbeddedSchemaMigration("004_publication_history.sql");

[Migration(5, "Technical search identifiers")]
public sealed class TechnicalSearchMigration() : EmbeddedSchemaMigration("005_search_technical_identifiers.sql");

[Migration(6, "Account and access administration")]
public sealed class AdministrationSchemaMigration() : EmbeddedSchemaMigration("006_administration.sql");

[Migration(7, "OpenID Connect federation")]
public sealed class OidcSchemaMigration() : EmbeddedSchemaMigration("007_oidc.sql");

[Migration(8, "Personal API access")]
public sealed class PersonalApiSchemaMigration() : EmbeddedSchemaMigration("008_personal_api.sql");

[Migration(9, "Server access recovery")]
public sealed class RecoverySchemaMigration() : EmbeddedSchemaMigration("009_recovery.sql");

[Migration(10, "Durable collaborative editing")]
public sealed class CollaborationSchemaMigration() : EmbeddedSchemaMigration("010_collaboration.sql");

[Migration(12)] public sealed class YouTrackSettingsSchemaMigration() : EmbeddedSchemaMigration("012_youtrack_settings.sql");
[Migration(13)] public sealed class AttachmentsSchemaMigration() : EmbeddedSchemaMigration("013_attachments.sql");
