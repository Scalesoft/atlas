using System.Globalization;
using Atlas.Server.Features.AccessAudit.Contracts;
using Atlas.Server.Features.Hub.Domain;

namespace Atlas.Server.Features.AccessAudit.Domain;

/// <summary>The append-only audit feed from migration 022, resolved to names. Read-only; the database view owns the joins.</summary>
public class AccessAuditView
{
    public virtual long Id { get; set; }
    public virtual DateTime OccurredAt { get; set; }
    public virtual Guid? ActorId { get; set; }
    public virtual string? ActorName { get; set; }
    public virtual string Action { get; set; } = "";
    public virtual Guid? UserId { get; set; }
    public virtual string? UserName { get; set; }
    public virtual Guid? GroupId { get; set; }
    public virtual string? GroupName { get; set; }
    public virtual Guid? SpaceId { get; set; }
    public virtual string? SpaceName { get; set; }
    public virtual int? Role { get; set; }
    public virtual bool? IsActive { get; set; }
    public virtual bool? IsWikiAdmin { get; set; }
    public virtual bool? CanIssueTokens { get; set; }
    /// <summary>Set for a vault or secret grant event; Role then holds a VaultAccessLevel, not a space role.</summary>
    public virtual Guid? VaultId { get; set; }
    /// <summary>The secret of a secret grant, release or request (WP4 V5).</summary>
    public virtual Guid? SecretId { get; set; }
    /// <summary>Set for a release or client report (secret.*): the SecretReleaseEvent columns; never a value.</summary>
    public virtual string? Result { get; set; }
    public virtual string? Field { get; set; }
    public virtual string? Operation { get; set; }
    public virtual string? Mode { get; set; }
    public virtual string? ModeSource { get; set; }
    public virtual string? MatchedNetwork { get; set; }
    public virtual bool? ReauthUsed { get; set; }
    public virtual string? SessionId { get; set; }
    public virtual string? RequestId { get; set; }
    public virtual string? ClientIp { get; set; }
    /// <summary>Set for an access request event (access_request.*).</summary>
    public virtual Guid? AccessRequestId { get; set; }
    /// <summary>The request reason (created) or the decision note (approved, rejected).</summary>
    public virtual string? Note { get; set; }
    /// <summary>Set for a protection event (protection.*): the mode of the setting row.</summary>
    public virtual string? ProtectionMode { get; set; }
    /// <summary>Set for a trusted network or proxy event (trusted_network.*, trusted_proxy.*): its name and range at that revision.</summary>
    public virtual string? TrustedAddressName { get; set; }
    public virtual string? TrustedAddressRange { get; set; }
    /// <summary>The security configuration version a protection, network, proxy or hop change activated.</summary>
    public virtual long? ConfigurationVersion { get; set; }
    /// <summary>The names of the event's vault and secret (metadata only).</summary>
    public virtual string? VaultName { get; set; }
    public virtual string? SecretTitle { get; set; }
    public virtual Guid? HubConnectionId { get; set; }
    public virtual string? ExternalGroupId { get; set; }
    public virtual string? ExternalUserId { get; set; }
    public virtual string? HubReason { get; set; }
    public virtual int? HubMode { get; set; }
    public virtual bool? HubBindingEnabled { get; set; }
    public virtual bool? HubGrantsWikiAdmin { get; set; }
    public virtual int? HubPreviousState { get; set; }
    public virtual int? HubCurrentState { get; set; }
    /// <summary>Set for an API permission event (api_permission.*): the level of that interval (none, read, draft, publish).</summary>
    public virtual string? ApiLevel { get; set; }
    /// <summary>The page of a page grant or of an API or MCP write; for a write it may name a page that does not exist (a 404).</summary>
    public virtual Guid? PageId { get; set; }
    /// <summary>The published title of that page, or null: never a working title.</summary>
    public virtual string? PageTitle { get; set; }
    /// <summary>A permanent delete (object.purged, page.purged, space.purged): the type and the title, none for a private object.</summary>
    public virtual string? ObjectTypeId { get; set; }
    public virtual string? ObjectTitle { get; set; }
    /// <summary>A backup change (backup.destination_changed, backup.downloaded): its kind (git.remote, git.key, archive.path,
    /// archive.complete, archive.download) and the destination it names (a URL, folder or file name).</summary>
    public virtual string? BackupKind { get; set; }
    public virtual string? BackupDestination { get; set; }
    /// <summary>Set for an API, MCP or assistant write (api.write, mcp.write, mcp.publish, assistant.write, assistant.publish): the
    /// ApiActivity facts, never content or an error text. The assistant has no token.</summary>
    public virtual string? ApiChannel { get; set; }
    public virtual string? ApiOperation { get; set; }
    public virtual string? ApiOutcome { get; set; }
    public virtual int? ApiResultVersion { get; set; }
    public virtual Guid? ApiTokenId { get; set; }
    public virtual string? ApiTokenName { get; set; }

    public virtual AccessAuditItem ToDto()
    {
        return new AccessAuditItem(Id.ToString(CultureInfo.InvariantCulture), OccurredAt,
            ActorId, ActorName, Action, UserId, UserName, GroupId, GroupName, SpaceId, SpaceName,
            RoleName(), IsActive, IsWikiAdmin, CanIssueTokens, VaultId, SecretId, Result, Field, Operation, Mode, ModeSource,
            MatchedNetwork, ReauthUsed, ClientIp, SessionId, RequestId, AccessRequestId, Note, ProtectionMode, TrustedAddressName, TrustedAddressRange,
            ConfigurationVersion?.ToString(CultureInfo.InvariantCulture), VaultName, SecretTitle,
            HubConnectionId, ExternalGroupId, ExternalUserId, HubReason,
            HubMode is { } mode ? ((HubConnectionMode)mode).ToString() : null, HubBindingEnabled, HubGrantsWikiAdmin,
            HubPreviousState is { } previous ? ((HubUpstreamAccountState)previous).ToString() : null,
            HubCurrentState is { } current ? ((HubUpstreamAccountState)current).ToString() : null, ApiLevel,
            ApiChannel, ApiOperation, ApiOutcome, ApiResultVersion, ApiTokenId, ApiTokenName, PageId, PageTitle, ObjectTypeId, ObjectTitle,
            BackupKind, BackupDestination);
    }

    /// <summary>A space or page role name, or the level of a vault or secret grant (discover, read, manage).</summary>
    private string? RoleName()
    {
        if (Role is not { } role)
        {
            return null;
        }
        return VaultId is null ? ((SpaceRole)role).ToString() : VaultAccessLevels.FromRole(role).Name();
    }
}
