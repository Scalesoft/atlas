using System.Text.RegularExpressions;

namespace Atlas.Server.Platform.Identity;

/// <summary>
/// Access predicates as query text, for the queries that decide them in SQL: the bodies of the database functions atlas_space_in_ai,
/// atlas_object_grant_role and atlas_may_create_below_page, which migration 2026110946 drops (3.6, no logic in the database).
/// AccessSqlParityTests pinned each against its function. The arguments are a parameter (:name, @name) or a qualified column
/// (alias."Column", {alias}."Column" of a criterion): an unqualified column would bind inside the predicate and decide nothing.
/// </summary>
public static partial class AccessSql
{
    /// <summary>Whether a space takes part in AI and MCP: no open setting excludes it (the default is included). The one definition:
    /// search, the index, suggestions and answer sources use it too.</summary>
    public static string SpaceInAi(string space) => $$"""
        NOT EXISTS (SELECT 1 FROM "SpaceAiSetting" space_ai WHERE space_ai."SpaceId"={{Checked(space)}} AND space_ai."ActiveTo" IS NULL AND NOT space_ai."Included")
        """;

    /// <summary>An account's role on a personal object from its grants (null for none): valid now, the account's own or its verified
    /// groups', and only for an active account.</summary>
    public static string ObjectGrantRole(string objectId, string account) => ObjectGrantRoleText(Checked(objectId), Checked(account));

    private static string ObjectGrantRoleText(string objectId, string account) => $$"""
        (SELECT max(object_grant."Role") FROM "EffectiveResourceGrant" object_grant
            WHERE object_grant."ObjectId" = {{objectId}}
              AND EXISTS (SELECT 1 FROM "UserAccount" grantee WHERE grantee."Id" = {{account}} AND grantee."IsActive")
              AND (object_grant."UserId" = {{account}} OR object_grant."GroupId" IN
                (SELECT grantee_group."GroupId" FROM "EffectiveGroupMember" grantee_group WHERE grantee_group."UserId" = {{account}})))
        """;

    /// <summary>The CTE <c>actor</c> ("Id", "IsWikiAdmin") of the account a query decides for (:actor with the :stamp of the request): one
    /// row while the account is active and that session not revoked, else none, so every check joined to it denies. WikiAdmin locally or
    /// by a verified Hub group (EffectiveWikiAdmin). The one definition: <c>WITH {ActorCte}, …</c>.</summary>
    public const string ActorCte = """
        actor AS MATERIALIZED (
          SELECT "Id",("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=:actor)) AS "IsWikiAdmin" FROM "UserAccount"
          WHERE "Id"=:actor AND "IsActive" AND "SecurityStamp"=:stamp
        )
        """;

    /// <summary>Whether the account (:actor with its :stamp, active) may create a page in :space: a WikiAdmin (local or by a verified Hub
    /// group) of an existing space, or a writer of a live, non-template page of it.</summary>
    public const string MayCreateBelowPage = $$"""
        WITH {{ActorCte}}
        SELECT EXISTS (SELECT 1 FROM actor a WHERE a."IsWikiAdmin" AND EXISTS (SELECT 1 FROM "Space" s WHERE s."Id"=:space))
          OR EXISTS (SELECT 1 FROM actor a CROSS JOIN "EffectivePageAccess" e JOIN "Page" p ON p."Id"=e."PageId"
            WHERE e."UserId"=:actor AND e."SpaceId"=:space AND e."Role">=2 AND p."SpaceId"=:space
              AND NOT p."IsTrashed" AND NOT p."IsTemplate") AS allowed
        """;

    private static string Checked(string argument) => Argument().IsMatch(argument) ? argument
        : throw new ArgumentException($"Not a parameter or a qualified column: {argument}", nameof(argument));

    // Not an alias of the predicates' own (space_ai, object_grant, grantee, grantee_group): it would bind inside them.
    [GeneratedRegex(@"^([:@]\w+|(?!(space_ai|object_grant|grantee|grantee_group)\.)(\w+|\{alias\})\.""\w+"")$")]
    private static partial Regex Argument();
}
