namespace Atlas.Server.Features.ModuleSettings.Contracts;

// Trusted startup modules register a descriptor and own their API, storage and UI contribution.
// A descriptor never contains executable URLs, secrets or arbitrary browser code.
public sealed record ModuleSettingsDescriptor(string Id, string Title, string Description, string SettingsUi, int Order, bool ShowInSetup);
