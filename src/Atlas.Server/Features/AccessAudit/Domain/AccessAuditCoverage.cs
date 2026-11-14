namespace Atlas.Server.Features.AccessAudit.Domain;

/// <summary>The migration journal row that says how far back the audit can speak. Read-only; FluentMigrator owns the table.</summary>
public class AccessAuditCoverage
{
    public virtual long Version { get; set; }
    public virtual DateTime AppliedAt { get; set; }
}
