using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.Pages.Contracts;

namespace Atlas.Server.Features.Pages.Commands;

/// <summary>POST /api/v1/pages, the read (<see cref="PageMarkdownCreate"/>): the create's decisions and the page links of the Markdown.</summary>
public sealed record PreparePageMarkdownCreate(CreatePageRequest Request, MarkdownLinkRequest Links);

/// <summary>The template as the actor reads it (null without one) and the resolved page links of the Markdown.</summary>
public sealed record PageMarkdownCreateBase(PageDto? Template, MarkdownLinkTargets Links);

/// <summary>PUT /api/v1/pages/{id}, the read (<see cref="PageMarkdownSaveRunner"/>): the working revision to merge into, at the expected version.</summary>
public sealed record PreparePageMarkdownSave(Guid PageId, int ExpectedVersion, MarkdownLinkRequest Links);

/// <summary>The reader's copy of the working revision with its title and version, and the resolved page links of the Markdown.</summary>
public sealed record PageMarkdownBase(string Title, string ContentJson, int Version, MarkdownLinkTargets Links);
