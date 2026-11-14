namespace Atlas.Server.Platform.Content;

/// <param name="Version">The page version projected; temporary heading ids are bound to it.</param>
/// <param name="ReadablePageTitle">
/// The title of a referenced page the actor may read, null otherwise. Without it every page reference is only "Odkaz na
/// stránku"; the title stored in the reference itself is never used.
/// </param>
public sealed record ProjectionOptions(int Version, Func<Guid, string?>? ReadablePageTitle = null);
