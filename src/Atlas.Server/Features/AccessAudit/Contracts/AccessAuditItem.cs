namespace Atlas.Server.Features.AccessAudit.Contracts;

public sealed record AccessAuditItem(string Id, DateTime OccurredAt, Guid? ActorId, string? ActorName, string Action,
    Guid? UserId, string? UserName, Guid? GroupId, string? GroupName, Guid? SpaceId, string? SpaceName,
    string? Role, bool? IsActive, bool? IsWikiAdmin, bool? CanIssueTokens,
    Guid? VaultId = null, Guid? SecretId = null, string? Result = null, string? Field = null, string? Operation = null, string? Mode = null,
    string? ModeSource = null, string? MatchedNetwork = null, bool? ReauthUsed = null, string? ClientIp = null, string? SessionId = null,
    string? RequestId = null, Guid? AccessRequestId = null, string? Note = null, string? ProtectionMode = null, string? TrustedAddressName = null,
    string? TrustedAddressRange = null, string? ConfigurationVersion = null, string? VaultName = null, string? SecretTitle = null,
    Guid? HubConnectionId = null, string? ExternalGroupId = null, string? ExternalUserId = null, string? HubReason = null,
    string? HubMode = null, bool? HubBindingEnabled = null, bool? HubGrantsWikiAdmin = null,
    string? HubPreviousState = null, string? HubCurrentState = null, string? ApiLevel = null,
    string? ApiChannel = null, string? ApiOperation = null, string? ApiOutcome = null, int? ApiResultVersion = null, Guid? ApiTokenId = null,
    string? ApiTokenName = null, Guid? PageId = null, string? PageTitle = null, string? ObjectTypeId = null, string? ObjectTitle = null,
    string? BackupKind = null, string? BackupDestination = null);
