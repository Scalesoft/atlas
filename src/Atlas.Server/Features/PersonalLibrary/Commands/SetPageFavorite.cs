using Atlas.Server.Features.PersonalLibrary.Contracts;

namespace Atlas.Server.Features.PersonalLibrary.Commands;

public sealed record SetPageFavorite(Guid PageId, SetFavorite Request);
