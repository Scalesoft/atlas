using Atlas.Server.Features.PageHistory.Contracts;

namespace Atlas.Server.Features.PageHistory.Commands;

public sealed record RestorePage(Guid Id, RestorePageRequest Request);
