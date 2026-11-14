namespace Atlas.Server.Features.ModuleSettings.Contracts;

public sealed record OidcSettingsView(Guid? Version, string PublicOrigin, OidcProviderView[] Providers, bool RestartRequired, bool BuiltinEnabled);
