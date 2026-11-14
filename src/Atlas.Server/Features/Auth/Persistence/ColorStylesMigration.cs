using FluentMigrator;

namespace Atlas.Server.Features.Auth.Persistence;

[Migration(31, "Five appearance color styles")]
public sealed class ColorStylesMigration() : EmbeddedSchemaMigration("031_appearance_color_styles.sql");
