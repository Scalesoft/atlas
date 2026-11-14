namespace Atlas.Server.Features.Pages.Contracts;

/// <summary>ChangeSummary describes the publication (all revisions since the previous one): one line of at most 500 characters, optional.
/// RefuseUnsavedLiveEdits: refuse (409 collaboration_unsaved) while anyone's live edits are not saved, which the publication of the saved
/// revision would leave out; the web sends it when publishing outside editing, where nobody has just saved what is on screen.</summary>
public sealed record PublishPageRequest(int ExpectedVersion, string? ChangeSummary = null, bool RefuseUnsavedLiveEdits = false);
