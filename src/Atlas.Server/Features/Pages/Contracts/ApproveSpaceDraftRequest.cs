namespace Atlas.Server.Features.Pages.Contracts;

public sealed record ApproveSpaceDraftRequest(int ExpectedVersion, Guid ExpectedRevisionId, string? ChangeSummary = null);
