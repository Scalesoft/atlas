using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.Mcp.Contracts;

namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>Step 1 of a content tool (plan C2): the working revision to merge into, if the actor may edit it at that version.</summary>
public sealed record GetMcpEditBase(Guid PageId, int ExpectedVersion, MarkdownLinkRequest Links);
