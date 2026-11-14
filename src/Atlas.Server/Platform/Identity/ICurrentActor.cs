namespace Atlas.Server.Platform.Identity;

/// <summary>The authenticated account behind the current unit of work. 401 when missing, inactive or with a rotated security stamp.</summary>
public interface ICurrentActor
{
    Task<UserAccount> RequireAsync(CancellationToken ct);

    /// <summary>The account id and security stamp the sign-in carries, without a query; 401 when absent.
    /// Authorization re-reads the account under its locks and compares the stamp there.</summary>
    ClaimedActor Claimed();
}

public sealed record ClaimedActor(Guid Id, string? SecurityStamp, Guid? BrowserSessionId = null);
