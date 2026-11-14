using FluentMigrator;

namespace Atlas.Server.Features.Ai.Settings;

[Migration(2026102040, "AI rerank profile kind and rerank usage")]
public sealed class AiRerankProfileMigration() : EmbeddedSchemaMigration("2026102040_ai_rerank_profile.sql");
