using FluentMigrator;

namespace Atlas.Server.Features.SearchGaps.Persistence;

[Migration(2026100720, "Reason of a search gap: zero, no_answer or reported")]
public sealed class SearchGapReasonsMigration() : EmbeddedSchemaMigration("2026100720_search_gap_reasons.sql");
