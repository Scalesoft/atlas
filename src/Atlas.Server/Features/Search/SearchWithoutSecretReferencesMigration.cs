using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(2026092730, "Search read models without password reference directives")]
public sealed class SearchWithoutSecretReferencesMigration() : EmbeddedSchemaMigration("2026092730_search_without_secret_references.sql");
