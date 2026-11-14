using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026110860, "The published search document of one page as a function the requirement conversion calls")]
public sealed class PublishedSearchDocumentFunctionMigration() : EmbeddedSchemaMigration("2026110860_published_search_document_function.sql");
