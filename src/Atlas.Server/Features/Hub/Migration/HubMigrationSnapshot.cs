using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atlas.Server.Features.Hub.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Hub.Migration;

internal static class HubMigrationSnapshot
{
    private static readonly JsonSerializerOptions s_json = new() { Converters = { new UtcDateTimeConverter() } };
    // One statement captures a consistent inventory; the content lock protects it until the short transaction ends.
    // lazy: full ACL inventory has an explicit 8 MiB ceiling; larger installations need a separately reviewed scoped digest.
    public static async Task<(HubMigrationInventory Inventory, string Digest)> ReadAsync(ISession session, HubConnectionRevision config, CancellationToken ct)
    {
        var json = await session.CreateSQLQuery("""
            SELECT cast(jsonb_build_object(
              'Users', (SELECT coalesce(jsonb_agg(to_jsonb(u) ORDER BY u."Id"),'[]') FROM
                (SELECT u."Id",u."DisplayName",u."IsActive",u."IsWikiAdmin",u."OidcAdminIssuer",u."SecurityStamp",
                  EXISTS(SELECT 1 FROM "BuiltinCredential" c WHERE c."UserId"=u."Id" AND length(c."PasswordHash")>0) AS "HasPassword"
                 FROM "UserAccount" u) u),
              'Groups', (SELECT coalesce(jsonb_agg(to_jsonb(g) ORDER BY g."Id"),'[]') FROM "UserGroup" g),
              'Members', (SELECT coalesce(jsonb_agg(to_jsonb(m) ORDER BY m."Id"),'[]') FROM "GroupMember" m),
              'EffectiveMembers', (SELECT coalesce(jsonb_agg(to_jsonb(m) ORDER BY m."Id"),'[]') FROM "EffectiveGroupMember" m),
              'Grants', (SELECT coalesce(jsonb_agg(to_jsonb(g) ORDER BY g."Id"),'[]') FROM
                (SELECT "Id","SpaceId",cast(NULL AS uuid) AS "PageId","UserId","GroupId","Role","StartAt","ExpiresAt","ActiveFrom","ActiveTo" FROM "SpaceGrant"
                 UNION ALL SELECT "Id",cast(NULL AS uuid),"PageId","UserId","GroupId","Role","StartAt","ExpiresAt","ActiveFrom","ActiveTo" FROM "ResourceGrant" WHERE "PageId" IS NOT NULL) g),
              'Pages', (SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY p."Id"),'[]') FROM
                (SELECT p."Id",p."SpaceId",p."OwnAccess",p."IsTrashed",p."IsTemplate",coalesce(r."Title",'') AS "Title"
                 FROM "Page" p LEFT JOIN "PageRevision" r ON r."PageId"=p."Id" AND r."ActiveTo" IS NULL) p),
              'Spaces', (SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY s."Id"),'[]') FROM (SELECT "Id","Name" FROM "Space") s),
              'Identities', (SELECT coalesce(jsonb_agg(to_jsonb(e) ORDER BY e."Id"),'[]') FROM "ExternalIdentity" e WHERE e."Issuer"=:issuer),
              'Bindings', (SELECT coalesce(jsonb_agg(to_jsonb(b) ORDER BY b."GroupId"),'[]') FROM "HubGroupBinding" b WHERE b."ConnectionId"=:connection),
              'EffectiveWikiAdmins', (SELECT coalesce(jsonb_agg(h."UserId" ORDER BY h."UserId"),'[]') FROM "EffectiveWikiAdmin" h),
              'States', (SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY s."ExternalIdentityId"),'[]') FROM "HubIdentityState" s),
              'BindingHistory', (SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY r."Id"),'[]') FROM "HubGroupBindingRevision" r),
              'PageModeHistory', (SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY r."Id"),'[]') FROM "PageAccessModeRevision" r),
              'GroupHistory', (SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY r."Id"),'[]') FROM "UserGroupRevision" r),
              'UserHistory', (SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY r."Id"),'[]') FROM "UserAccountRevision" r)
            ) AS text)
            """).SetParameter("connection", config.ConnectionId).SetParameter("issuer", config.Issuer).UniqueResultAsync<string>(ct);
        if (Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024)
            throw new ApiException(409, "Inventura je příliš velká pro bezpečný interaktivní náhled.", "hub_migration_too_large");
        // Deserialization deliberately drops stamps, sync state and history metadata from the public export.
        return (JsonSerializer.Deserialize<HubMigrationInventory>(json, s_json)!, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }

    public static void Validate(PreviewHubMigration request, HubMigrationInventory inventory, DateTime now)
    {
        ValidateShape(request);
        ValidateGroups(request.Groups, inventory);
        ValidateGrantRemovals(request, inventory);
        ValidateAdministrators(request, inventory, now);
        ValidatePages(request.PageBoundaries, inventory);
        var identities = ActiveIdentities(inventory);
        if (identities.GroupBy(x => x.UserId).Any(x => x.Count() != 1) || identities.GroupBy(x => x.Subject, StringComparer.Ordinal).Any(x => x.Count() != 1))
            throw new ApiException(409, "Hub účet má více aktivních vazeb identity.", "hub_identity_conflict");
    }

    private static void ValidateShape(PreviewHubMigration request)
    {
        if (request.Groups is null || request.RemoveSpaceGrantIds is null || request.RemovePageGrantIds is null ||
            request.RemoveWikiAdminUserIds is null || request.PageBoundaries is null) throw Invalid();
        if (request.Groups.Any(x => x is null) || request.PageBoundaries.Any(x => x is null)) throw Invalid();
        if (request.Groups.Length + request.PageBoundaries.Length > 1000 ||
            request.RemoveSpaceGrantIds.Length + request.RemovePageGrantIds.Length + request.RemoveWikiAdminUserIds.Length > 10000)
            throw Invalid();
        Unique(request.Groups.Select(x => x.LocalGroupId));
        Unique(request.Groups.Select(x => x.ExternalGroupId));
        Unique(request.RemoveSpaceGrantIds);
        Unique(request.RemovePageGrantIds);
        Unique(request.RemoveWikiAdminUserIds);
        Unique(request.PageBoundaries.Select(x => x.PageId));
    }

    private static void ValidateGroups(HubMigrationGroup[] groups, HubMigrationInventory inventory)
    {
        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.ExternalGroupId) || group.ExternalGroupId.Length > 500 || group.ExternalGroupId.Any(char.IsControl) ||
                !inventory.Groups.Any(x => x.Id == group.LocalGroupId) || inventory.Bindings.Any(x => x.GroupId == group.LocalGroupId || x.ExternalGroupId == group.ExternalGroupId))
                throw Invalid();
        }
    }

    private static void ValidateGrantRemovals(PreviewHubMigration request, HubMigrationInventory inventory)
    {
        foreach (var id in request.RemoveSpaceGrantIds)
            if (!inventory.Grants.Any(x => x.Id == id && x.SpaceId is not null && x.ActiveTo is null)) throw Invalid();
        foreach (var id in request.RemovePageGrantIds)
            if (!inventory.Grants.Any(x => x.Id == id && x.PageId is not null && x.ActiveTo is null)) throw Invalid();
    }

    private static void ValidateAdministrators(PreviewHubMigration request, HubMigrationInventory inventory, DateTime now)
    {
        ValidateRecoveryAdministrator(request, inventory);
        ValidatePermanentSpaceAdministrators(request, inventory, now);
    }

    private static void ValidateRecoveryAdministrator(PreviewHubMigration request, HubMigrationInventory inventory)
    {
        foreach (var id in request.RemoveWikiAdminUserIds)
            if (!inventory.Users.Any(x => x.Id == id && x.IsWikiAdmin && x.OidcAdminIssuer is null)) throw Invalid();
        if (!inventory.Users.Any(x => x.IsActive && x.IsWikiAdmin && x.OidcAdminIssuer is null && x.HasPassword && !request.RemoveWikiAdminUserIds.Contains(x.Id)))
            throw new ApiException(409, "Migrace musí zachovat místního nouzového správce s heslem.", "hub_recovery_required");
    }

    private static void ValidatePermanentSpaceAdministrators(PreviewHubMigration request, HubMigrationInventory inventory, DateTime now)
    {
        var removed = request.RemoveSpaceGrantIds.ToHashSet();
        var permanent = inventory.Grants.Where(x => x.SpaceId is not null && x.ActiveTo is null && x.Role == 3 && x.ExpiresAt is null && (x.StartAt is null || x.StartAt <= now));
        foreach (var space in permanent.Where(x => removed.Contains(x.Id)).Select(x => x.SpaceId).Distinct())
            if (!permanent.Any(x => x.SpaceId == space && !removed.Contains(x.Id)))
                throw new ApiException(409, "Prostor musí mít alespoň jedno explicitní trvalé přiřazení správce.");
    }

    private static void ValidatePages(HubMigrationPage[] pages, HubMigrationInventory inventory)
    {
        foreach (var page in pages)
        {
            if (page.Grants is null || page.Grants.Any(x => x is null) || page.Grants.Length > 1000 || !inventory.Pages.Any(x => x.Id == page.PageId && !x.IsTrashed)) throw Invalid();
            Unique(page.Grants.Select(x => x.GroupId));
            foreach (var grant in page.Grants)
                if (!inventory.Groups.Any(x => x.Id == grant.GroupId) || grant.Role is not ("Reader" or "Writer" or "Admin")) throw Invalid();
        }
    }

    public static HubMigrationIdentity[] ActiveIdentities(HubMigrationInventory inventory) => inventory.Identities
        .Where(x => x.ActiveTo is null && inventory.Users.Any(u => u.Id == x.UserId && u.IsActive)).ToArray();

    public static bool GrantDeadlineCrossed(HubMigrationInventory inventory, DateTime startedAt, DateTime now) =>
        inventory.Grants.Where(x => x.ActiveTo is null).Any(x =>
            x.StartAt > startedAt && x.StartAt <= now || x.ExpiresAt > startedAt && x.ExpiresAt <= now);

    public static HubMigrationCopiedGrant[] CopiedDefaults(HubMigrationInventory inventory, HubMigrationTicket ticket)
    {
        var copied = new List<HubMigrationCopiedGrant>();
        foreach (var boundary in ticket.Request.PageBoundaries)
        {
            var page = inventory.Pages.Single(x => x.Id == boundary.PageId);
            if (page.OwnAccess) continue;
            foreach (var grant in SpaceDefaultsToCopy(inventory, ticket, page.SpaceId))
                if (!HasRetainedPageGrant(inventory, ticket.Request, page.Id, grant))
                    copied.Add(new(page.Id, grant.Id, grant.UserId, grant.GroupId, grant.Role));
        }
        return copied.ToArray();
    }

    private static IEnumerable<HubMigrationGrant> SpaceDefaultsToCopy(HubMigrationInventory inventory, HubMigrationTicket ticket, Guid spaceId) =>
        inventory.Grants.Where(g => g.ActiveTo is null && g.SpaceId == spaceId && g.Role < 3 &&
            !ticket.Request.RemoveSpaceGrantIds.Contains(g.Id) && (g.ExpiresAt is null || g.ExpiresAt > ticket.StartedAt));

    private static bool HasRetainedPageGrant(HubMigrationInventory inventory, PreviewHubMigration request, Guid pageId, HubMigrationGrant grant) =>
        inventory.Grants.Any(existing => existing.PageId == pageId && existing.ActiveTo is null && existing.Role >= grant.Role &&
            !request.RemovePageGrantIds.Contains(existing.Id) &&
            (grant.UserId is not null && existing.UserId == grant.UserId || grant.GroupId is not null && existing.GroupId == grant.GroupId));

    public static HubMigrationMemberDiff[] MembershipDiffs(HubMigrationInventory inventory, HubMigrationTicket ticket) =>
        inventory.Bindings.Select(x => new HubMigrationGroup(x.GroupId, x.ExternalGroupId)).Concat(ticket.Request.Groups).Select(group =>
            new HubMigrationMemberDiff(group.LocalGroupId, group.ExternalGroupId, ticket.Request.Groups.Any(x => x.LocalGroupId == group.LocalGroupId),
                inventory.Members.Where(x => x.GroupId == group.LocalGroupId && x.ActiveTo is null).Select(x => x.UserId).Order().ToArray(),
                ticket.Observations.Where(x => x.User.State == Directory.HubDirectoryAccountState.Active && x.User.Groups.Any(g => g.Id == group.ExternalGroupId))
                    .Select(x => x.UserId).Order().ToArray())).ToArray();

    public static HubMigrationRoute[] Routes(HubMigrationInventory inventory, HubMigrationTicket ticket, bool after)
    {
        var members = ProjectedMembers(inventory, ticket, after);
        var ownPages = inventory.Pages.Where(x => x.OwnAccess).Select(x => x.Id).ToHashSet();
        var externalGroups = inventory.Bindings.Select(x => x.GroupId).ToHashSet();
        if (after)
        {
            ownPages.UnionWith(ticket.Request.PageBoundaries.Select(x => x.PageId));
            externalGroups.UnionWith(ticket.Request.Groups.Select(x => x.LocalGroupId));
        }
        var routes = new List<HubMigrationRoute>();
        foreach (var grant in ProjectedGrants(inventory, ticket, after))
            AddGrantRoutes(routes, inventory, grant, members, ownPages, externalGroups);
        AddWikiAdminRoutes(routes, inventory, ticket, after, members);
        return routes.ToArray();
    }

    private static HashSet<(Guid GroupId, Guid UserId)> ProjectedMembers(HubMigrationInventory inventory, HubMigrationTicket ticket, bool after)
    {
        var members = inventory.EffectiveMembers.Select(x => (x.GroupId, x.UserId)).ToHashSet();
        if (after)
        {
            foreach (var diff in MembershipDiffs(inventory, ticket))
            {
                members.RemoveWhere(x => x.GroupId == diff.LocalGroupId);
                if (inventory.Bindings.SingleOrDefault(x => x.GroupId == diff.LocalGroupId)?.Enabled == false) continue;
                members.UnionWith(diff.AfterUserIds.Select(x => (diff.LocalGroupId, x)));
            }
        }
        return members;
    }

    private static IEnumerable<HubMigrationGrant> ProjectedGrants(HubMigrationInventory inventory, HubMigrationTicket ticket, bool after)
    {
        var boundaryPages = ticket.Request.PageBoundaries.Select(x => x.PageId).ToHashSet();
        var grants = inventory.Grants.Where(x => x.ActiveTo is null && (x.StartAt is null || x.StartAt <= ticket.StartedAt) &&
            (x.ExpiresAt is null || x.ExpiresAt > ticket.StartedAt));
        if (after)
            grants = grants.Where(x => !ticket.Request.RemoveSpaceGrantIds.Contains(x.Id) && !ticket.Request.RemovePageGrantIds.Contains(x.Id) &&
                (x.PageId is null || !boundaryPages.Contains(x.PageId.Value)))
                .Concat(ticket.Request.PageBoundaries.SelectMany(p => p.Grants.Select(g => new HubMigrationGrant(Guid.Empty, null, p.PageId, null, g.GroupId,
                    Role(g.Role), null, null, ticket.StartedAt, null))));
        return grants;
    }

    private static void AddGrantRoutes(List<HubMigrationRoute> routes, HubMigrationInventory inventory, HubMigrationGrant grant,
        HashSet<(Guid GroupId, Guid UserId)> members, HashSet<Guid> ownPages, HashSet<Guid> externalGroups)
    {
        var users = grant.UserId is { } userId ? [userId] : members.Where(x => x.GroupId == grant.GroupId).Select(x => x.UserId).ToArray();
        var source = grant.UserId is not null ? "personal" : externalGroups.Contains(grant.GroupId!.Value) ? "hub-group" : "local-group";
        foreach (var user in users.Where(id => inventory.Users.Any(x => x.Id == id && x.IsActive)))
        {
            routes.Add(new(user, grant.PageId is null ? "space" : "page", grant.PageId ?? grant.SpaceId, grant.Role, source, grant.Id == Guid.Empty ? null : grant.Id, grant.GroupId));
            if (grant.SpaceId is { } space)
                AddInheritedPageRoutes(routes, inventory.Pages, grant, user, source, space, ownPages);
        }
    }

    private static void AddInheritedPageRoutes(List<HubMigrationRoute> routes, HubMigrationPageState[] pages, HubMigrationGrant grant,
        Guid user, string source, Guid space, HashSet<Guid> ownPages)
    {
        foreach (var page in pages.Where(p => p.SpaceId == space && !p.IsTrashed && (grant.Role == 3 || !ownPages.Contains(p.Id))))
            routes.Add(new(user, "page", page.Id, page.IsTemplate && grant.Role < 3 ? 1 : grant.Role,
                grant.Role == 3 ? source + ":space-admin" : source + ":space-default", grant.Id, grant.GroupId));
    }

    private static void AddWikiAdminRoutes(List<HubMigrationRoute> routes, HubMigrationInventory inventory, HubMigrationTicket ticket,
        bool after, HashSet<(Guid GroupId, Guid UserId)> members)
    {
        foreach (var user in inventory.Users.Where(x => x.IsActive))
        {
            var sources = new List<(string Source, Guid? Group)>();
            if (user.IsWikiAdmin && (!after || !ticket.Request.RemoveWikiAdminUserIds.Contains(user.Id)))
                sources.Add((user.OidcAdminIssuer is null ? "wiki-admin-local" : "wiki-admin-oidc", null));
            foreach (var binding in inventory.Bindings.Where(x => x.Enabled && x.GrantsWikiAdmin && members.Contains((x.GroupId, user.Id))))
                sources.Add(("wiki-admin-hub", binding.GroupId));
            foreach (var source in sources)
            {
                routes.Add(new(user.Id, "wiki", null, 3, source.Source, null, source.Group));
                routes.AddRange(inventory.Pages.Where(x => !x.IsTrashed).Select(p => new HubMigrationRoute(user.Id, "page", p.Id, 3, source.Source, null, source.Group)));
                routes.AddRange(inventory.Spaces.Select(s => new HubMigrationRoute(user.Id, "space", s.Id, 3, source.Source, null, source.Group)));
            }
        }
    }

    public static int Role(string role) => role switch { "Reader" => 1, "Writer" => 2, "Admin" => 3, _ => throw Invalid() };
    private static void Unique<T>(IEnumerable<T> values) { if (values.Distinct().Count() != values.Count()) throw Invalid(); }
    private static ApiException Invalid() => new(400, "Neplatný nebo nejednoznačný výběr migrace.", "hub_migration_invalid");

    // PostgreSQL JSON uses explicit offsets; DateTime's default parser converts them to the host's local time.
    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDateTimeOffset().UtcDateTime;
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToUniversalTime());
    }
}
