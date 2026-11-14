using FluentMigrator;

namespace Atlas.Server.Features.AiSuggestions.Persistence;

[Migration(2026100570, "Semantic suggestions: link candidates, [[ ordering, similar pages, placement and template suggestions")]
public sealed class SemanticSuggestionsMigration() : EmbeddedSchemaMigration("2026100570_semantic_suggestions.sql");
