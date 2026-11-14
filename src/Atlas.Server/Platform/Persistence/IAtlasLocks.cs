namespace Atlas.Server.Platform.Persistence;

/// <summary>Row and advisory locks that NHibernate LockMode cannot express. The only raw SQL outside mappings and migrations.</summary>
/// <remarks>
/// Row locks answer "does the row exist" with a bool; the caller owns the 404 text so each feature keeps its own wording.
/// LockSpaceSharedAsync is the one exception: its 404 is shared by every content write and predates this interface.
/// LockAccountSharedAsync ignores a missing account: the caller re-reads the account afterwards and decides.
/// Row locks are native SQL and see only flushed state: flush the session before locking a row it created.
/// A lock the current transaction already holds (the content lock, a space FOR SHARE) is not requested again.
/// </remarks>
public interface IAtlasLocks
{
    /// <summary>Shared content lock 731145012: readers and ordinary writes.</summary>
    Task AcquireContentSharedAsync(CancellationToken ct);

    /// <summary>Exclusive content lock 731145012: administration writes that must exclude all content work.</summary>
    Task AcquireContentExclusiveAsync(CancellationToken ct);

    /// <summary>Exclusive advisory lock on a caller-chosen numeric key (import, recovery, YouTrack, personal API).</summary>
    Task AcquireAdvisoryAsync(long key, CancellationToken ct);

    /// <summary>Exclusive advisory lock derived from a text key with hashtextextended(key, 812), the seed the external identity lock uses today.</summary>
    Task AcquireAdvisoryAsync(string textKey, CancellationToken ct);

    /// <summary>FOR SHARE on a space; throws 404 when the space does not exist.</summary>
    Task LockSpaceSharedAsync(Guid spaceId, CancellationToken ct);

    /// <summary>3.6-I: the space's „Publikace“ default as LockSpaceSharedAsync read it in this transaction, so a page read needs no
    /// second read of the space; null when this transaction does not hold the space FOR SHARE.</summary>
    Features.Pages.Domain.PublicationMode? SharedSpacePublicationMode(Guid spaceId) => null;

    /// <summary>FOR UPDATE on a space. False when the space does not exist.</summary>
    Task<bool> LockSpaceForUpdateAsync(Guid spaceId, CancellationToken ct);

    /// <summary>FOR SHARE on a page. False when the page does not exist.</summary>
    Task<bool> LockPageSharedAsync(Guid pageId, CancellationToken ct);

    /// <summary>FOR UPDATE on a page. False when the page does not exist.</summary>
    Task<bool> LockPageForUpdateAsync(Guid pageId, CancellationToken ct);

    /// <summary>FOR SHARE on an account.</summary>
    Task LockAccountSharedAsync(Guid userId, CancellationToken ct);

    /// <summary>FOR SHARE on an account, returning its row as it is under the lock (one statement). Null when missing.</summary>
    Task<UserAccount?> ReadAccountSharedAsync(Guid userId, CancellationToken ct);

    /// <summary>FOR SHARE on a page, returning its row as it is under the lock (one statement). Null when missing.</summary>
    Task<Page?> ReadPageSharedAsync(Guid pageId, CancellationToken ct);

    /// <summary>FOR UPDATE on an account. False when the account does not exist.</summary>
    Task<bool> LockAccountForUpdateAsync(Guid userId, CancellationToken ct);

    /// <summary>Names the actor of the transaction (TransactionActor) for the history rows the code writes. When a browser session is
    /// supplied, rejects its revocation using a new statement snapshot; the caller must already hold the account lock.</summary>
    Task SetActorAsync(Guid actorId, CancellationToken ct, Guid? browserSessionId = null);

    /// <summary>FOR SHARE on the SecurityConfiguration row, returning its Version and ProxyHopLimit as they are under the lock. Security
    /// changes take the row FOR UPDATE, so a decision holding this lock reads one consistent configuration version.</summary>
    Task<(long Version, int ProxyHopLimit)> ReadSecurityConfigurationSharedAsync(CancellationToken ct);

    /// <summary>FOR KEY SHARE on the given groups in id order; returns the ids that exist.</summary>
    Task<IReadOnlyList<Guid>> LockGroupsForKeyShareAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken ct);

    /// <summary>FOR UPDATE on the given pages in id order, in one statement; returns the ids that exist.</summary>
    Task<IReadOnlyList<Guid>> LockPagesForUpdateAsync(IReadOnlyCollection<Guid> pageIds, CancellationToken ct);
}
