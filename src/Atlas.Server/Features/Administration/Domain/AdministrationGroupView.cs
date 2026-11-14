using Atlas.Server.Features.Administration.Contracts;

namespace Atlas.Server.Features.Administration.Domain;

/// <summary>Current group name with the ActiveFrom of its current revision as version. Read-only CQRS projection.</summary>
public class AdministrationGroupView
{
    public virtual Guid Id { get; set; }
    public virtual string Name { get; set; } = "";
    public virtual DateTime ActiveFrom { get; set; }
    public virtual string Source { get; set; } = "local";
    public virtual string? HubBaseUrl { get; set; }
    public virtual string? ExternalGroupId { get; set; }

    public virtual GroupSummary ToSummary()
    {
        return new GroupSummary(Id, Name, AdministrationVersion.Format(ActiveFrom), Source,
            HubBaseUrl is not null && ExternalGroupId is not null ? Hub.Persistence.HubSettingsReader.GroupUrl(HubBaseUrl, ExternalGroupId) : null);
    }
}
