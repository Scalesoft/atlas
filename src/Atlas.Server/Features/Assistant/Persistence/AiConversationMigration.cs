using FluentMigrator;

namespace Atlas.Server.Features.Assistant.Persistence;

[Migration(2026100700, "AI conversations, messages and source dependencies with atlas_ai_dependency_status")]
public sealed class AiConversationMigration() : EmbeddedSchemaMigration("2026100700_ai_conversations.sql");
