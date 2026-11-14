using NHibernate;
using Atlas.Server.Features.PageOrganization.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageAccess.Commands;

public sealed record SetPageAccessMode(Guid PageId, UpdatePageAccessMode Request);

/// <summary>
/// Switches a page between the space defaults and own access (WP3 S5). CAS on the current PageAccessModeRevision (its ActiveFrom).
/// Turning own access on copies the space default Reader/Writer assignments into page grants with the same validity (D6), so
/// nobody who reads or edits the page now loses it; the space Admin needs no copy (D4). Turning it off returns the page to exactly
/// the space defaults: its page grants are closed (the response of the impact confirmation names who loses the page). Lock order
/// of access writes (IContentAccess): content shared, space FOR UPDATE, actor, page FOR UPDATE; the audit, the mode history and
/// the share link closures name the locked actor.
/// </summary>
public sealed class SetPageAccessModeHandler(ISession session, OrganizationLocks locks, AccessChanges accessChanges)
{
    public async Task<PageAccessModeResult> Handle(SetPageAccessMode command, CancellationToken ct)
    {
        var (page, actor) = await locks.LockPageAsync(command.PageId, false, AccessOperation.ManageAccess, ct);
        AdministrationVersion.Expect(await CurrentVersionAsync(session, page.Id, ct), command.Request.ExpectedVersion);
        if (page.OwnAccess == command.Request.OwnAccess)
        {
            return await ResultAsync(page, ct);
        }
        if (command.Request.OwnAccess)
        {
            await CopySpaceDefaultsAsync(page, actor.Id, ct);
            page.OwnAccess = true;
            await PageAccessModeHistory.RecordAsync(session, page, actor.Id, ct);
        }
        else
        {
            page.OwnAccess = false;
            await PageAccessModeHistory.RecordAsync(session, page, actor.Id, ct);
            var closed = await session.CreateSQLQuery("""
                UPDATE "ResourceGrant" SET "ActiveTo" = greatest(clock_timestamp(), "ActiveFrom" + interval '1 microsecond')
                WHERE "PageId" = :page AND "ActiveTo" IS NULL
                RETURNING "Id"
                """).SetParameter("page", page.Id).ListAsync<Guid>(ct);
            await AccessAuditLog.ClosedAsync(session, AccessAuditLog.ResourceGrant, [.. closed], actor.Id, ct);
        }
        await session.FlushAsync(ct);
        accessChanges.Changed();
        return await ResultAsync(page, ct);
    }

    internal static Task<DateTime?> CurrentVersionAsync(ISession session, Guid pageId, CancellationToken ct)
    {
        return session.CreateSQLQuery("""
            SELECT "ActiveFrom" FROM "PageAccessModeRevision" WHERE "PageId" = :page AND "ActiveTo" IS NULL
            """).SetParameter("page", pageId).UniqueResultAsync<DateTime?>(ct);
    }

    /// <summary>D6. A subject that already has a page grant keeps it unless its space default role is higher: then the page grant
    /// is replaced by the copy, so the effective role stays the same. Already expired defaults are not copied.</summary>
    private async Task CopySpaceDefaultsAsync(Page page, Guid actorId, CancellationToken ct)
    {
        const string defaults = """
            FROM "SpaceGrant" s
            WHERE s."SpaceId" = :space AND s."ActiveTo" IS NULL AND s."Role" < 3
                AND (s."ExpiresAt" IS NULL OR statement_timestamp() < s."ExpiresAt")
            """;
        var closed = await session.CreateSQLQuery("""
            UPDATE "ResourceGrant" r SET "ActiveTo" = greatest(clock_timestamp(), r."ActiveFrom" + interval '1 microsecond')
            """ + defaults + """
                AND r."PageId" = :page AND r."ActiveTo" IS NULL AND r."Role" < s."Role"
                AND (r."UserId" = s."UserId" OR r."GroupId" = s."GroupId")
            RETURNING r."Id"
            """).SetParameter("page", page.Id).SetParameter("space", page.SpaceId).ListAsync<Guid>(ct);
        await AccessAuditLog.ClosedAsync(session, AccessAuditLog.ResourceGrant, [.. closed], actorId, ct);
        // Temporal.NextBoundary in SQL: after the subject's latest closed interval on this page (EX_ResourceGrant_*NoOverlap).
        var opened = await session.CreateSQLQuery("""
            INSERT INTO "ResourceGrant"("Id", "PageId", "UserId", "GroupId", "Role", "StartAt", "ExpiresAt", "ActiveFrom")
            SELECT gen_random_uuid(), :page, s."UserId", s."GroupId", s."Role", s."StartAt", s."ExpiresAt",
                greatest(clock_timestamp(), (SELECT max(p."ActiveTo") FROM "ResourceGrant" p
                    WHERE p."PageId" = :page AND (p."UserId" = s."UserId" OR p."GroupId" = s."GroupId")) + interval '1 microsecond')
            """ + defaults + """
                AND NOT EXISTS (SELECT 1 FROM "ResourceGrant" r WHERE r."PageId" = :page AND r."ActiveTo" IS NULL
                    AND (r."UserId" = s."UserId" OR r."GroupId" = s."GroupId"))
            RETURNING "Id"
            """).SetParameter("page", page.Id).SetParameter("space", page.SpaceId).ListAsync<Guid>(ct);
        await AccessAuditLog.OpenedAsync(session, AccessAuditLog.ResourceGrant, [.. opened], actorId, ct);
    }

    private async Task<PageAccessModeResult> ResultAsync(Page page, CancellationToken ct)
    {
        var version = await CurrentVersionAsync(session, page.Id, ct);
        return new PageAccessModeResult(page.OwnAccess ? "custom" : "inherit", AdministrationVersion.Format(version!.Value));
    }
}
