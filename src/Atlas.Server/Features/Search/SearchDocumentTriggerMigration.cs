using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(2026102810, "Search documents: re-projected only when the publication or the space of a page changes, or on a rebuild")]
public sealed class SearchDocumentTriggerMigration() : EmbeddedSchemaMigration("2026102810_search_document_trigger.sql");
