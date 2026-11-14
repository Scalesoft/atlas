namespace Atlas.Server.Features.Access.Domain;

/// <summary>Writes the grants of one kind by calling the kind's existing command handler in the same transaction, so the generic
/// and the old endpoints enforce the same validation, authority, fresh sign-in, CAS and audit.</summary>
public interface IAccessGrantWriter
{
    /// <summary>Sets, changes or (Level null) revokes the grant of one user or group on the resource.</summary>
    Task ApplyAsync(Guid resourceId, AccessGrantChange change, CancellationToken ct);
}
