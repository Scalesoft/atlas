using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.AccessAudit.Domain;

namespace Atlas.Server.Features.AccessAudit.Persistence;

public sealed class AccessAuditViewMap : ClassMapping<AccessAuditView>
{
    public AccessAuditViewMap()
    {
        Table("`AccessAuditView`");
        Mutable(false);
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.OccurredAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActorId);
        Property(x => x.ActorName);
        Property(x => x.Action);
        Property(x => x.UserId);
        Property(x => x.UserName);
        Property(x => x.GroupId);
        Property(x => x.GroupName);
        Property(x => x.SpaceId);
        Property(x => x.SpaceName);
        Property(x => x.Role);
        Property(x => x.IsActive);
        Property(x => x.IsWikiAdmin);
        Property(x => x.CanIssueTokens);
        Property(x => x.VaultId);
        Property(x => x.SecretId);
        Property(x => x.Result);
        Property(x => x.Field);
        Property(x => x.Operation);
        Property(x => x.Mode);
        Property(x => x.ModeSource);
        Property(x => x.MatchedNetwork);
        Property(x => x.ReauthUsed);
        Property(x => x.SessionId);
        Property(x => x.RequestId);
        Property(x => x.ClientIp);
        Property(x => x.AccessRequestId);
        Property(x => x.Note);
        Property(x => x.ProtectionMode);
        Property(x => x.TrustedAddressName);
        Property(x => x.TrustedAddressRange);
        Property(x => x.ConfigurationVersion);
        Property(x => x.VaultName);
        Property(x => x.SecretTitle);
        Property(x => x.HubConnectionId);
        Property(x => x.ExternalGroupId);
        Property(x => x.ExternalUserId);
        Property(x => x.HubReason);
        Property(x => x.HubMode);
        Property(x => x.HubBindingEnabled);
        Property(x => x.HubGrantsWikiAdmin);
        Property(x => x.HubPreviousState);
        Property(x => x.HubCurrentState);
        Property(x => x.ApiLevel);
        Property(x => x.PageId);
        Property(x => x.PageTitle);
        Property(x => x.ObjectTypeId);
        Property(x => x.ObjectTitle);
        Property(x => x.BackupKind);
        Property(x => x.BackupDestination);
        Property(x => x.ApiChannel);
        Property(x => x.ApiOperation);
        Property(x => x.ApiOutcome);
        Property(x => x.ApiResultVersion);
        Property(x => x.ApiTokenId);
        Property(x => x.ApiTokenName);
    }
}
