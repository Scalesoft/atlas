using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026101610, "Content channel assistant: changes, publications, openings and audited writes without a token")]
public sealed class AssistantChannelMigration() : EmbeddedSchemaMigration("2026101610_assistant_channel.sql");
