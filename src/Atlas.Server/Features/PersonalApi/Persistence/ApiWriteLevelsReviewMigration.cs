using FluentMigrator;

namespace Atlas.Server.Features.PersonalApi.Persistence;

/// <summary>4.0: the review notice for the tokens and levels 2026110684 widened to write (see the SQL).</summary>
[Migration(2026111140, "4.0: review notice for the token scopes and levels widened to write by 2026110684")]
public sealed class ApiWriteLevelsReviewMigration() : EmbeddedSchemaMigration("2026111140_api_write_levels_review.sql");
