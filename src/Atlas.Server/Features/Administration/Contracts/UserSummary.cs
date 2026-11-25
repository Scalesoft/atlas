namespace Atlas.Server.Features.Administration.Contracts;

public sealed record UserSummary(Guid Id, string Username, string DisplayName, bool IsActive, bool IsWikiAdmin,
    bool HasBuiltinCredential, string CredentialState, string Source, string Version, Guid? ContactId = null);
