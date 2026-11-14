using Atlas.Server.Features.PageSource.Contracts;

namespace Atlas.Server.Features.PageSource.Commands;

public sealed record ReplacePageSource(Guid Id, ReplacePageSourceRequest Request);
