using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026100610, "Channel and personal API token of page revisions, publications and openings")]
public sealed class ContentChannelMigration() : EmbeddedSchemaMigration("2026100610_content_channel.sql");
