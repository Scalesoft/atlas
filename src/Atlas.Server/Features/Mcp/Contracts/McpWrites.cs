using Atlas.Server.Features.PageLinks;

namespace Atlas.Server.Features.Mcp.Contracts;

/// <summary>What a content tool merges into: the working revision of a page the actor may edit, at the expected version.</summary>
public sealed record McpEditBase(Guid PageId, Guid SpaceId, int Version, string Title, string ContentJson, MarkdownLinkTargets Links);

/// <summary>A draft write done: the page, its space and its version after the write, and what the conversion changed on the way, the contacts the save linked to the directory or created (contacts spec §3) and the chips it made of names (<c>link: "auto"</c>).</summary>
public sealed record McpWriteResult(Guid PageId, Guid SpaceId, int Version, IReadOnlyList<string> Warnings, Atlas.Server.Features.Contacts.ContactsReport? Contacts = null,
    Atlas.Server.Platform.Linking.LinkReport? Links = null, Atlas.Server.Features.PageDecorations.PageDecorationDto? Decoration = null);
