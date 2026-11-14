using Atlas.Server.Features.Administration.Contracts;

namespace Atlas.Server.Features.Administration.Domain;

/// <summary>Current account metadata joined with its credential state. Read-only CQRS projection, not a stored copy.</summary>
public class AdministrationUserView
{
    public virtual Guid Id { get; set; }
    public virtual string Username { get; set; } = "";
    public virtual string DisplayName { get; set; } = "";
    public virtual bool IsActive { get; set; }
    public virtual bool IsWikiAdmin { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual bool HasBuiltinCredential { get; set; }
    public virtual bool HasPassword { get; set; }

    public virtual UserSummary ToSummary()
    {
        var state = HasBuiltinCredential ? HasPassword ? "active" : "pending" : "none";
        var source = HasBuiltinCredential ? "builtin" : "external";
        return new UserSummary(Id, Username, DisplayName, IsActive, IsWikiAdmin, HasBuiltinCredential,
            state, source, AdministrationVersion.Format(ActiveFrom));
    }
}
