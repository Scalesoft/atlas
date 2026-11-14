using Atlas.Server.Platform.Live;

namespace Atlas.Server.Features.Objects.Queries;

/// <summary>The objects of a view as its data says now, for its view session (no account: the session decides each participant).</summary>
public sealed record GetViewMembers(string Kind, Guid ViewId);

/// <summary>Not <see cref="Supported"/>: no enabled module registers the view kind. Empty: the view is missing or trashed.
/// Otherwise the view's own object first, then the objects it shows that are of a registered type and not trashed.</summary>
public sealed record ViewMemberList(bool Supported, IReadOnlyList<ViewMember> Members);
