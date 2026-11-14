using Atlas.Server.Features.Pages.Contracts;

namespace Atlas.Server.Features.Pages.Commands;

public sealed record PublishPage(Guid Id, PublishPageRequest Request, SpaceDraftReview? Review = null);
