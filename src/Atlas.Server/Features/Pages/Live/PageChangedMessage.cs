namespace Atlas.Server.Features.Pages.Live;

/// <summary>PageChanged on <c>page:{pageId}</c>: the page changed, Kind <c>working</c> (a saved working revision, only to who may
/// edit it) or <c>published</c> (its publication or its mode, a move, the trash or a restore). No content: the client reads the page again.
/// A working change names the revision's <paramref name="Title"/> (its receivers may read it): an open page sees another writer's rename
/// without reading anything, and its own checkpoint coming back as nothing new.</summary>
public sealed record PageChangedMessage(Guid PageId, string Kind, string? Title = null);
