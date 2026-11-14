namespace Atlas.Server.Features.Setup.Contracts;

/// <summary>The shape returned when the database is not reachable; it deliberately omits the fields the wizard cannot know yet.</summary>
public sealed record SetupUnavailableDto(bool DatabaseReady, bool RequiresSetup, bool BootstrapTokenRequired, string Error, string Version);
