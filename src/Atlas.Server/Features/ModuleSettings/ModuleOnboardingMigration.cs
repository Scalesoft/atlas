using FluentMigrator;

namespace Atlas.Server.Features.ModuleSettings;

[Migration(15)]
public sealed class ModuleOnboardingMigration() : EmbeddedSchemaMigration("015_module_onboarding.sql");
