using Atlas.Server.Features.Backups.Git;
using Atlas.Server.Features.Backups.Installation;
using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Features.StorageProfiles;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Backups.Commands;

/// <summary>
/// SEC1-2, SEC3-1: a backup holds every private note and personal board by design (a keyless restore), so where it goes and what it
/// carries are security changes. A new Git repository, a new deploy key, a new folder of the ZIP backups, the switch of the ZIP backups
/// to the complete mode (its platform part holds the notes in plain form) and an edit of the storage profile the ZIP backups use
/// (AuthorizeBackupProfile, SEC3-3) need a fresh sign-in (kind <c>backup.destination</c>). Saving the same again, or none, asks for
/// nothing. Sent before the save; the approval it answers is written by the save, in the save's transaction (SEC3-4), as an access
/// audit event naming the new destination (migration 2026103010).
/// </summary>
/// <param name="Kind"><c>git.remote</c>, <c>git.key</c> or <c>archive.path</c>.</param>
/// <param name="Destination">The new URL or folder; for a profile's folder <c>profile:{id}/{folder}</c>; ignored for a key.</param>
/// <param name="Mode">For <c>archive.path</c>: the requested mode, so a switch to <c>complete</c> is approved too.</param>
/// <param name="Enabled">The requested switch: enabling Git, or the complete mode, creates a missing backup key, and issuing one is a
/// security change of its own (<c>backup.key</c>, SEC-T-3) even when the destination stays.</param>
public sealed record AuthorizeBackupDestination(string Kind, string Destination, string? Mode = null, bool Enabled = false);

/// <summary>SEC3-3: an edit of a storage profile moves the ZIP backups that use it (their destination is computed from the profile at
/// every run), so a changed root, server or account of that profile is a destination change.</summary>
public sealed record AuthorizeBackupProfile(Guid ProfileId, StorageProfileRequest Request);

/// <summary>SEC3-1: a complete archive holds every private note, so taking one out needs a fresh sign-in; <paramref name="Record"/>
/// writes its backup.downloaded audit event (the download itself), without it the call only asks for the sign-in (the web, before it
/// starts the download).</summary>
public sealed record AuthorizeBackupDownload(Guid RunId, bool Record);

/// <summary>What a fresh sign-in approved; <see cref="WriteAsync"/> writes it inside the transaction of the save that makes it.</summary>
/// <param name="IssuesKey">The save creates the missing backup key and records <c>backup.key</c> with its ID (<see cref="WriteKeyAsync"/>).</param>
public sealed record BackupDestinationApproval(Guid ActorId, IReadOnlyList<(string Kind, string Destination)> Changes, bool IssuesKey = false)
{
    /// <summary>A save is about to show a new backup key: refused unless the fresh sign-in covered it (the key went missing only after
    /// the approval), then audited with the key's ID, never the key.</summary>
    public static async Task WriteKeyAsync(BackupDestinationApproval? approval, ISession session, string keyId, CancellationToken ct)
    {
        if (approval is null)
        {
            return; // A direct service call (tests); every HTTP save passes its approval.
        }
        if (!approval.IssuesKey)
        {
            throw new ApiException(409, "Klíč zálohy se mezitím změnil. Uložte nastavení znovu.");
        }
        await AuthorizeBackupDestinationHandler.RecordAsync(session, approval.ActorId, AuthorizeBackupDestinationHandler.BackupKey, keyId, ct);
    }

    public async Task WriteAsync(ISession session, CancellationToken ct)
    {
        foreach (var (kind, destination) in Changes)
        {
            await AuthorizeBackupDestinationHandler.RecordAsync(session, ActorId, kind, destination, ct);
        }
    }
}

public static class AuthorizeBackupDestinationHandler
{
    public const string GitRemote = "git.remote";
    public const string GitKey = "git.key";
    public const string ArchivePath = "archive.path";
    public const string ArchiveComplete = "archive.complete";
    public const string ArchiveDownload = "archive.download";
    public const string BackupKey = "backup.key";
    private const string CompleteMode = "complete";

    public static async Task<BackupDestinationApproval> Handle(AuthorizeBackupDestination command, ModuleSettingsAccess access, ISecurityChangeGuard guard,
        ISession session, CancellationToken ct)
    {
        var actor = await access.RequireWikiAdminAsync(ct);
        var changes = new List<(string, string)>();
        var issuesKey = false;
        switch (command.Kind)
        {
            case GitRemote:
                var url = command.Destination.Trim();
                var git = await session.GetAsync<GitBackupSettings>(1, LockMode.Upgrade, ct);
                if (url.Length > 0 && url != git.RemoteUrl)
                {
                    changes.Add((GitRemote, url));
                }
                // Without a deploy key the save refuses the switch with its own 400.
                issuesKey = command.Enabled && git.PublicKey is not null
                    && (await session.GetAsync<BackupSettings>(1, LockMode.Upgrade, ct)).ProtectedBackupKey is null;
                break;
            case GitKey:
                // The key is for the repository configured now; a new one is written to whoever holds that repository.
                changes.Add((GitKey, (await session.GetAsync<GitBackupSettings>(1, LockMode.Upgrade, ct)).RemoteUrl));
                break;
            case ArchivePath:
                var destination = command.Destination.Trim();
                var archive = await session.GetAsync<BackupSettings>(1, LockMode.Upgrade, ct);
                var current = archive.StorageProfileId is { } profile ? $"profile:{profile:D}/{archive.Folder}" : archive.Destination;
                if (destination.Length > 0 && destination != current)
                {
                    changes.Add((ArchivePath, destination));
                }
                if (command.Mode == CompleteMode && archive.Mode != CompleteMode)
                {
                    changes.Add((ArchiveComplete, destination.Length > 0 ? destination : current));
                }
                issuesKey = command.Enabled && command.Mode == CompleteMode && archive.ProtectedBackupKey is null;
                break;
            default:
                throw new ApiException(400, "Neznámý cíl zálohy.");
        }
        if (issuesKey && changes.Count == 0)
        {
            await guard.RequireAsync(actor, new SecurityChange("backup.destination", Guid.Empty), ct);
        }
        return await ApproveAsync(guard, actor, changes, ct) with { IssuesKey = issuesKey };
    }

    public static async Task<BackupDestinationApproval> Handle(AuthorizeBackupProfile command, ModuleSettingsAccess access, ISecurityChangeGuard guard,
        ISession session, CancellationToken ct)
    {
        var actor = await access.RequireWikiAdminAsync(ct);
        var archive = await session.GetAsync<BackupSettings>(1, LockMode.Upgrade, ct);
        var profile = archive.StorageProfileId == command.ProfileId ? await session.GetAsync<StorageProfile>(command.ProfileId, ct) : null;
        string destination;
        try
        {
            destination = StorageProfilePaths.NormalizeDestination(command.Request.Destination);
        }
        catch (Exception ex) when (ex is ArgumentException or ApiException)
        {
            // The save refuses it with its own 400.
            return new(actor.Id, []);
        }
        var username = command.Request.Username?.Trim() ?? "";
        var domain = command.Request.Domain?.Trim() ?? "";
        if (profile is null || profile.Destination == destination && profile.Username == username && profile.Domain == domain)
        {
            return new(actor.Id, []);
        }
        var account = username.Length == 0 ? "" : $" ({(domain.Length > 0 ? domain + "\\" : "")}{username})";
        return await ApproveAsync(guard, actor, [(ArchivePath, StorageProfilePaths.Destination(destination, archive.Folder) + account)], ct);
    }

    public static async Task Handle(AuthorizeBackupDownload command, ModuleSettingsAccess access, ISecurityChangeGuard guard, ISession session,
        CancellationToken ct)
    {
        var actor = await access.RequireWikiAdminAsync(ct);
        var run = await session.GetAsync<BackupRun>(command.RunId, ct);
        // A missing or expired archive answers its own 404 or 410 in the download; only a complete one carries the notes.
        if (run is not { Mode: CompleteMode, Status: "succeeded", FileName: { } file })
        {
            return;
        }
        await guard.RequireAsync(actor, new SecurityChange("backup.destination", Guid.Empty), ct);
        if (command.Record)
        {
            await RecordAsync(session, actor.Id, ArchiveDownload, file, ct);
        }
    }

    /// <summary>The archive destination of a request in the form <see cref="Handle(AuthorizeBackupDestination, ModuleSettingsAccess, ISecurityChangeGuard, ISession, CancellationToken)"/> compares.</summary>
    public static string ArchiveDestination(BackupSettingsRequest request)
        => request.StorageProfileId is { } profile ? $"profile:{profile:D}/{StorageProfilePaths.NormalizeFolder(request.Folder)}" : request.Destination;

    /// <summary>The fact and its access audit event: backup.downloaded for a download, backup.destination_changed otherwise.</summary>
    internal static async Task RecordAsync(ISession session, Guid actorId, string kind, string destination, CancellationToken ct)
    {
        var id = await session.CreateSQLQuery("""INSERT INTO "BackupDestinationChange"("ActorId","Kind","Destination") VALUES(:actor,:kind,:destination) RETURNING "Id" """)
            .SetParameter("actor", actorId).SetParameter("kind", kind)
            .SetParameter("destination", destination.Length > 2000 ? destination[..2000] : destination)
            .UniqueResultAsync<long>(ct);
        await AccessAuditLog.RecordAsync(session, actorId, kind == ArchiveDownload ? "backup.downloaded" : "backup.destination_changed",
            AccessAuditLog.BackupDestinationChange, id, ct);
    }

    private static async Task<BackupDestinationApproval> ApproveAsync(ISecurityChangeGuard guard, UserAccount actor, List<(string, string)> changes,
        CancellationToken ct)
    {
        if (changes.Count > 0)
        {
            await guard.RequireAsync(actor, new SecurityChange("backup.destination", Guid.Empty), ct);
        }
        return new(actor.Id, changes);
    }
}
