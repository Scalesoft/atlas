using FluentMigrator;

namespace Atlas.Server.Features.Auth.Persistence;

[Migration(2026100960, "Calm appearance accents and the reduced-motion preference")]
public sealed class AppearanceAccentsMotionMigration() : EmbeddedSchemaMigration("2026100960_appearance_accents_motion.sql");
