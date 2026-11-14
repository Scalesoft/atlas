using FluentMigrator;

namespace Atlas.Server.Features.AiIndex.Persistence;

[Migration(2026100540, "Knowledge index: generations, pages, chunks and the trigger-fed IndexTask queue")]
public sealed class KnowledgeIndexMigration() : EmbeddedSchemaMigration("2026100540_knowledge_index.sql");
