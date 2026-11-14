using Atlas.Server.Features.PageOrganization.Contracts;

namespace Atlas.Server.Features.PageOrganization.Commands;

public sealed record RenamePage(Guid PageId, RenamePageRequest Request);
