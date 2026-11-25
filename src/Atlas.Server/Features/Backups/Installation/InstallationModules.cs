using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Hub.Commands;
using Atlas.Server.Features.ModuleSettings.Domain;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.YouTrack.Commands;
using Atlas.Server.Platform.Backup;

namespace Atlas.Server.Features.Backups.Installation;

/// <summary>
/// The module backups of this distribution: the one place that says which module backs up which table (format v7,
/// docs/backup-format-v7.md). A table no module lists must be in <see cref="BackupCoverage"/>; the coverage test enforces both,
/// and that every module restores after the modules its rows reference.
/// </summary>
/// <remarks>
/// Content and the configuration a working restore needs, nothing operational (<see cref="BackupCoverage"/>). Secure tables hold
/// accounts, credentials, grants, security and integration configuration and vaults; everything else is content anyone with access
/// to the repository may read, as with the 2.x Git backup.
/// </remarks>
public static class InstallationModules
{
    /// <summary>A queued run of the job the restore asks for (one already queued stays).</summary>
    private static string Queue(string jobId) => "INSERT INTO \"JobRun\"(\"JobId\",\"Trigger\",\"Reason\",\"State\") "
        + $"VALUES ('{jobId}','event','Obnova zálohy','queued') ON CONFLICT DO NOTHING";

    /// <summary>The pages' derived rows after a restore. The search documents of every page are rebuilt from its revisions
    /// (SearchDocuments, in C# since migration 2026110935); the page links and the contact mentions, derived by code, are requested
    /// as rebuild jobs; contributions are the newest revision of each author, as a written revision keeps them
    /// (ContentRowEffects). The 3.4 and 3.6 content conversions are requested again: a backup taken before or during its run brings
    /// pages in the 3.3 shape back, and a run over converted content reads nothing.</summary>
    public static readonly string PagesRebuild = Search.SearchDocuments.RebuildAllSql + "; "
        + Queue(PageLinks.PageLinkRebuildJob.JobId) + "; "
        + Queue(Contacts.PageObjectMentionRebuildJob.JobId) + "; "
        + "INSERT INTO \"PageContribution\"(\"UserId\",\"PageId\",\"LastRevisionVersion\") "
        + "SELECT \"AuthorId\", \"PageId\", max(\"Version\") FROM \"PageRevision\" GROUP BY \"AuthorId\", \"PageId\"; "
        + Queue(Pages.RequirementsOverviewJob.JobId) + "; "
        + Queue(Pages.RequirementsHeadingsJob.JobId) + "; "
        + Queue(Collaboration.LiveSeedsJob.JobId);

    /// <summary>The current revision of every account and group after a restore: their history is operational and starts empty, but the
    /// profile and the administration read an account or a group through its open revision, so each gets one from its restored row.</summary>
    public const string IdentityRebuild = "INSERT INTO \"UserAccountRevision\"(\"Id\",\"UserId\",\"Username\",\"DisplayName\",\"IsActive\",\"IsWikiAdmin\",\"Email\",\"ActiveFrom\") "
        + "SELECT gen_random_uuid(), u.\"Id\", u.\"Username\", u.\"DisplayName\", u.\"IsActive\", u.\"IsWikiAdmin\", u.\"Email\", now() FROM \"UserAccount\" u "
        + "WHERE NOT EXISTS (SELECT 1 FROM \"UserAccountRevision\" r WHERE r.\"UserId\" = u.\"Id\" AND r.\"ActiveTo\" IS NULL); "
        + "INSERT INTO \"UserGroupRevision\"(\"Id\",\"GroupId\",\"Name\",\"ActiveFrom\") SELECT gen_random_uuid(), g.\"Id\", g.\"Name\", now() FROM \"UserGroup\" g "
        + "WHERE NOT EXISTS (SELECT 1 FROM \"UserGroupRevision\" r WHERE r.\"GroupId\" = g.\"Id\" AND r.\"ActiveTo\" IS NULL)";

    /// <summary>The entity catalog after a restore: a queued run of its rebuild, which the restart the restore asks for starts.</summary>
    public static readonly string EntityCatalogRebuild = Queue(Entities.EntityCatalogRebuildJob.JobId);

    /// <summary>
    /// The data migrations since format v7 that fill or fix backed-up rows from other rows, or start work over them, by their schema
    /// version (PER-S-2). A backup restores its rows as written, so after the restore of a backup of an older schema each one newer
    /// than the backup runs again, oldest first. A new migration of that kind adds its statement here; one that only rebuilds
    /// derived tables needs nothing, the modules rebuild them.
    /// </summary>
    public static readonly IReadOnlyList<(long Schema, string Sql)> DataMigrations =
    [
        (2026102930, "UPDATE \"ContentResource\" r SET \"Title\" = b.\"Title\" FROM \"Board\" b WHERE b.\"Id\" = r.\"Id\""),
        (2026103011, "UPDATE \"PagePublication\" u SET \"ChangeSummary\" = NULL FROM \"Page\" p "
            + "WHERE p.\"Id\" = u.\"PageId\" AND p.\"PurgedAt\" IS NOT NULL AND u.\"ChangeSummary\" IS NOT NULL"),
        (2026103035, "UPDATE \"PageCollaboration\" c SET \"PendingCount\" = p.\"Count\", \"PendingBytes\" = p.\"Bytes\" "
            + "FROM (SELECT u.\"PageId\", count(*) AS \"Count\", sum(octet_length(u.\"Payload\")) AS \"Bytes\" FROM \"CollaborationUpdate\" u "
            + "JOIN \"PageCollaboration\" s ON s.\"PageId\" = u.\"PageId\" AND s.\"Generation\" = u.\"Generation\" "
            + "WHERE u.\"Sequence\" > s.\"SnapshotSequence\" GROUP BY u.\"PageId\") p WHERE c.\"PageId\" = p.\"PageId\""),
        // A 3.3 backup brings its deployment boards, requirement cards and lane marks back: the migration's own script removes them again.
        // Its board search function went with migration 2026110935: the boards' refresh is the C# statement now.
        (2026110470, Replace(Script("2026110470_drop_deployment_boards"), "SELECT atlas_refresh_board_search(\"Id\") FROM changed_board;",
            Boards.Domain.BoardSearch.RefreshSql("IN (SELECT \"Id\" FROM changed_board)") + ";")),
        // Its boards' envelopes follow it, as the board trigger did until 2026110930: the deleted boards' go, a moved sequence is the version.
        (2026110470, "DELETE FROM \"ContentResource\" r WHERE r.\"TypeId\" = 'atlas.board' AND NOT EXISTS (SELECT 1 FROM \"Board\" b WHERE b.\"Id\" = r.\"Id\"); "
            + "UPDATE \"ContentResource\" r SET \"Version\" = b.\"Sequence\", \"UpdatedAt\" = now() FROM \"Board\" b WHERE b.\"Id\" = r.\"Id\" AND r.\"Version\" <> b.\"Sequence\""),
        // A backup from before the notes brings accounts without a row, which would read as new: they have seen 3.4.3, as the migration says.
        (2026110540, "INSERT INTO \"UserReleaseNotes\"(\"UserId\",\"SeenVersion\") SELECT \"Id\", '3.4.3' FROM \"UserAccount\" ON CONFLICT DO NOTHING"),
        // A backup from before 3.6 brings revisions without versions: each becomes a version of its own, as the migration made them.
        (2026110630, "UPDATE \"PageRevision\" r SET \"GroupId\" = r.\"Id\", \"GroupStartedAt\" = r.\"ActiveFrom\", \"BaseRevisionId\" = p.\"Previous\" "
            + "FROM (SELECT \"Id\", lag(\"Id\") OVER (PARTITION BY \"PageId\" ORDER BY \"Version\") AS \"Previous\" FROM \"PageRevision\") p "
            + "WHERE p.\"Id\" = r.\"Id\" AND r.\"GroupId\" IS NULL"),
        // A backup from before the fill brings requirements without their catalog's service: the job is requested again (it waits for the
        // heading conversion). A backup taken after it is left alone, so a service a person cleared since is not filled again.
        (2026111150, Script("2026111150_requirement_systems")),
        // A backup from before 5.0 brings the company provider under its legacy id: its revisions follow the migration.
        (2026111400, "UPDATE \"AiProfileRevision\" SET \"Provider\" = 'gateway' WHERE lower(\"Provider\") = 'strata'")
        // 2026110480 (the conversion of requirement summaries) needs nothing here: the pages' rebuild requests it after every restore.
    ];

    private static string Replace(string script, string old, string replacement)
        => script.Contains(old, StringComparison.Ordinal) ? script.Replace(old, replacement, StringComparison.Ordinal)
            : throw new InvalidOperationException($"The script no longer holds: {old}");

    private static string Script(string name)
    {
        using var stream = typeof(InstallationModules).Assembly.GetManifestResourceStream($"Atlas.Server.Persistence.Migrations.{name}.sql")
            ?? throw new InvalidOperationException($"Migration {name} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static BackupCatalog Catalog(IDataProtectionProvider protection, AttachmentFiles attachments, VaultFolders vaults, ILogger logger) => new(
    [
        new TableBackup("identity", 1, [], Secure(
            "UserAccount", "UserGroup", "GroupMember", "BuiltinCredential", "ExternalIdentity", "UserAppearancePreference", "UserApiPermission",
            "NotificationPreference", // which kinds of notifications an account switched off (inbox spec §8)
            "UserReleaseNotes", // the release notes an account has seen (Co je nového)
            // A personal API token is a credential of its account, like a password: without it every integration stops after a restore.
            "PersonalApiToken", "PersonalApiTokenScope", "PersonalApiTokenChannel",
            // A signed-out browser session stays revoked after a full restore: the key ring that renews its cookie may come back too.
            "RevokedBrowserSession",
            "SecurityConfiguration", "TrustedAddress", "OidcSettingsRevision", "OidcProviderScope", "OidcAdminMapping", "HubConnection",
            "HubGroupBinding", "McpSetting", "TrashCleanupSetting")
            .Append(new BackupTable("OidcProviderSettings", true, Purpose("ProtectedSecret", OidcProtection.Purpose)))
            .Append(new BackupTable("HubConnectionRevision", true, Purpose("ProtectedToken", HubConfigurationHandler.TokenPurpose))).ToList(),
            protection, [new AccountNamesPart()], IdentityRebuild),
        new TableBackup("platform", 1, ["identity"], Plain(
            "Workspace", "ModuleOnboarding", "Space", "SpaceMetadataRevision", "SpaceOrganization", "SpaceCover", "SpacePin",
            "SpaceExternalSharingPolicy", "ContentResource", "ContactPhoto", "SpaceTrashCleanupSetting"), protection),
        new TableBackup("pages", 1, ["identity", "platform"], [
            new BackupTable("Page"), ApiChannel("PageRevision"), ApiChannel("PagePublication"), .. Plain("PageOrganizationRevision", "PageAccessModeRevision", "Tag", "TagAlias", "TagRelation", "PageTag", "SkillRating", "PageDecoration", "PageVerification",
            "PageFavorite", "TreeReference"),
            // Migration 2026110670 dropped the live state's unread Markdown and plain-text projections.
            new BackupTable("PageCollaboration", Retired: ["ProjectionMarkdown", "ProjectionText"]), .. Plain("CollaborationUpdate", "CollaborationSecretReference",
            // The old Wiki.js paths keep resolving (search aliases) and a new import updates the pages instead of copying them.
            "WikiImportSource", "WikiImportDestination", "WikiImportPage", "WikiImportCheckpoint")], protection,
            rebuild: PagesRebuild),
        new TableBackup("attachments", 1, ["pages"], Plain("Attachment", "PageRevisionAttachment"), protection, [new AttachmentBlobsPart(attachments, logger)]),
        new TableBackup("vaults", 1, ["platform"], [
            new BackupTable("VaultLocation", true, Purpose("ProtectedPassword", VaultProtection.LocationPasswordPurpose)),
            new BackupTable("Vault", true, Purpose("ProtectedKdbxPassword", VaultProtection.KdbxPasswordPurpose)),
            new BackupTable("VaultSecret", true),
            new BackupTable("ProtectionSetting", true)
        ], protection, [new VaultFilesPart(vaults)]),
        new TableBackup("access", 1, ["pages", "vaults"], Secure("SpaceGrant", "ResourceGrant", "PageShareLink"), protection),
        new TableBackup("settings", 1, ["pages"], [
            new BackupTable("StorageProfile", true, Purpose("ProtectedPassword", BackupService.DestinationPasswordPurpose)),
            new BackupTable("StorageProfileSpace", true),
            new BackupTable("YouTrackConfiguration", true, Purpose("ProtectedToken", SaveYouTrackSettingsHandler.TokenPurpose)),
            new BackupTable("PagePdfExport")
        ], protection),
        new TableBackup("ai", 1, ["pages"], [
            .. Plain("AiSetting", "AiFeatureSettings", "EditorAssistSetting", "AiProfileAssignment", "AiSavedProfile", "GroupAiPolicy", "UserAiPolicy",
                "UserAiPreference", "SpaceAiSetting"),
            new BackupTable("AiProfileRevision", true, Purpose("ProtectedApiKey", SaveAiProfileHandler.ApiKeyPurpose))
        ], protection),
        new TableBackup("boards", 1, ["platform", "pages"], Plain("Board", "BoardItem"), protection,
            rebuild: Boards.Domain.BoardSearch.RefreshSql("IN (SELECT \"Id\" FROM \"Board\")")),
        // The entity catalog is rebuilt from the restored pages and contacts through the outbox (migration 2026102950).
        new TableBackup("objects", 1, ["platform"], Plain("ObjectChange"), protection, rebuild: EntityCatalogRebuild),
        // Comments on any object (comments spec §7); their search documents are derived.
        new TableBackup("comments", 1, ["identity", "platform"], Plain("Comment", "CommentMention"), protection,
            rebuild: Comments.Persistence.CommentWrites.SearchRefreshSql("IN (SELECT \"Id\" FROM \"Comment\")")),
        // Projector v1 (spec 2026-10-27 §4): the catalog marks; the requirement blocks are page content, their index is derived.
        new TableBackup("projector", 1, ["identity", "pages"], Plain("ProjectorCatalogPage"), protection,
            // The index is derived and the restore empties it: rebuilt in code at once (the 384 pages of production in about 2 s), so the
            // uses, counts and warnings that read it are right as soon as the restore ends.
            rebuildInCode: (session, ct) => Projector.Persistence.ProjectorIndex.EnsureFreshAsync(session, null, ct))
    ]);

    /// <summary>A content table with the channel it was written through: "api" and "mcp" need the personal API token, which is in
    /// the secure part; restored without it, the content stays and its channel becomes "legacy" (unknown, as before channels).</summary>
    private static BackupTable ApiChannel(string table)
        => new(table, Cleared: new Dictionary<string, IReadOnlyDictionary<string, string>> { ["ApiTokenId"] = new Dictionary<string, string> { ["Channel"] = "legacy" } });

    private static List<BackupTable> Plain(params string[] tables) => tables.Select(x => new BackupTable(x)).ToList();

    private static List<BackupTable> Secure(params string[] tables) => tables.Select(x => new BackupTable(x, true)).ToList();

    private static Dictionary<string, string> Purpose(string column, string purpose) => new() { [column] = purpose };
}

/// <summary>
/// Tables that are deliberately not backed up: the backup holds content and the configuration a working restore needs, nothing that
/// is a log, queue, session, token, cache, index, counter or delivery state (owner, 27. 10. 2026). A restore empties the derived and
/// operational tables and keeps the host's and the running process's (<see cref="BackupCoverage.Running"/>).
/// </summary>
public static class BackupCoverage
{
    /// <summary>Computed from the backed-up rows: rebuilt after a restore (RebuildAsync), or created again by the administrator.</summary>
    public static readonly IReadOnlySet<string> Derived = Set(
        "SearchDocument", "WorkingSearchDocument", "BoardSearchDocument", // search projections, rebuilt by the pages and boards modules
        "CommentSearchDocument",                     // search documents of comments, rebuilt by the comments module
        "PageLink",                                  // links parsed from published revisions, rebuilt by the job pages.links-rebuild
        "PageObjectMention",                         // contacts mentioned in published and working revisions, rebuilt by the job contacts.mentions-rebuild
        "EntityOccurrence",                          // the entity catalog, rebuilt by the objects module through the outbox
        "PageMentionNotice",                         // accounts the pages' publications told of their mention, rebuilt silently with the entity catalog
        "ProjectorRequirementIndex", "ProjectorIndexedRevision", // requirement blocks of working and published revisions, rebuilt by the projector module
        "PageContribution",                          // each author's newest revision of a page, rebuilt from the revisions
        "ContentReference", "ContentReferenceSource", // what content names (people, pages, issues), extracted again by the linking sweep
        "IndexGeneration", "IndexTask", "KnowledgePage", "KnowledgeChunk", "PageNeighbor"); // the AI index, built again by the administrator

    /// <summary>Of the running installation, not content: a restore starts them empty, except the running process's (<see cref="Running"/>).</summary>
    public static readonly IReadOnlySet<string> Operational = Set(
        "AccessAuditEvent", "SecretReleaseEvent", "PageAccessEvent", "ApiActivity", "HubChange", "ProjectorCatalogEvent", "ObjectPurgeEvent", "BackupDestinationChange", // audit logs (owner: they stay operational)
        "UserAccountRevision", "UserGroupRevision", "SecurityConfigurationRevision", "TrustedAddressRevision",
        "VaultLocationRevision", "HubGroupBindingRevision",  // change history of accounts and configuration: audit, the current state is backed up
        "UserSignIn", "ReauthChallenge", "OidcStateToken",  // sign-in log and the sessions' one-time state
        "BuiltinAccountToken", "ServerRecoveryToken",        // one-time invitation, reset and recovery tokens
        "OidcFreshAuthStatus", "HubIdentityState",           // cached results of checks against the identity providers
        "PageView", "PageVisit", "AiUsage", "AiInteractionDaily", "AiDeletedDailyUsage", // usage counters and recently visited pages
        "SearchQueryLog", "SearchQueryLogKey", "SearchGapResolution", // the search miss log, its hashing key and what was resolved in it
        "AiConversation", "AiMessage", "AiToolProposal", "AiSourceDependency", "AiFeedback", // assistant chat log and its feedback
        "AiDuplicateDismissal",                              // dismissed suggestions of the AI index, which is built again
        "Job", "JobRun",                                     // the background jobs: their pause and next time, and the log of their runs
        "wolverine_incoming_envelopes", "wolverine_outgoing_envelopes", "wolverine_dead_letters", "wolverine_dead_letter_history",
        "wolverine_nodes", "wolverine_node_assignments", "wolverine_node_records",
        "wolverine_control_queue", "wolverine_agent_restrictions", // Wolverine's message storage: queued and scheduled work of this process, not content
        "ApiLevelsReview",                                   // a one-time review notice of the security overview
        "VaultAccessRequest",                                // access request workflow; the grants it produced are backed up
        "BoardOperation",                                    // the board sync log: a restored board makes clients reload (resync_required)
        "WikiImportBatch",                                   // expiring import previews and their reports
        "Notification",                                      // the inbox: delivery state, a restore starts it empty (inbox spec §8)
        "GitBackupPage",                                    // what the Git repository holds: reset with the content
        "ContactLinkRun",                                   // the progress of the contacts' link migration, run again from its report
        "RequirementConversionBackup",                      // originals of the 3.6 requirement conversion, for a rollback on this installation only
        "RequirementConversionUse",                         // the approved budget the 3.6 requirement conversion used here; a restore converts again
        "AppLog");                                          // the server's own log (Systémový log)

    /// <summary>Belong to the host, not to the content: the migration journal and the backups' own configuration, key and history.</summary>
    public static readonly IReadOnlySet<string> Host = Set("SchemaVersion", "BackupSettings", "BackupRun", "GitBackupSettings");

    /// <summary>Operational tables the running process works in, its jobs, its log and Wolverine's storage: a restore keeps them as the host's and
    /// leaves their triggers alone, so a job running meanwhile keeps its run and the jobs keep their pause.</summary>
    public static bool Running(string table) => table is "Job" or "JobRun" or "AppLog" || table.StartsWith("wolverine_", StringComparison.Ordinal);

    private static HashSet<string> Set(params string[] tables) => new(tables, StringComparer.Ordinal);
}
