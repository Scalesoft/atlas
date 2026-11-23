using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(2026112300, "Search: the trigram word similarity gets its real cost, so the planner uses the trigram indexes")]
public sealed class TrigramOperatorCostMigration() : EmbeddedSchemaMigration("2026112300_trigram_operator_cost.sql");
