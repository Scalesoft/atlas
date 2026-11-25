using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Atlas.Server.Tests.Conventions;

/// <summary>
/// AGENTS.md, Testing, "How a test gets its database": databases and hosts come from the shared fixtures (TestDatabases,
/// DisposableSchema, AtlasTestHost, AtlasHttpSite), which reuse the databases of a run and count what they cost (TestCosts). A test
/// that creates a database or a host of its own pays the full price every time and is invisible to those counts, which is how the
/// suite got slow. This test reads the sources: the statements and builders below stay in the files that have them today, and a
/// database that starts empty (every migration applied by the test, and never reused) is for migration and upgrade tests.
/// The lists are exact both ways, so a file that stops needing its entry must leave it.
/// </summary>
[Trait("Category", "Guard")]
public sealed partial class TestFixtureCostConventionTests
{
    /// <summary>What only the named files may contain.</summary>
    private static readonly (Regex Pattern, string What, string[] Files)[] Reserved =
    [
        (DatabaseStatement(), "CREATE or DROP DATABASE: take a schema from DisposableSchema", ["Platform/TestDatabases.cs"]),
        (WebFactory(), "a WebApplicationFactory: use AtlasHttpSite", ["Platform/AtlasHttpSite.cs"]),
        (HostBuilder(), "a host of its own: use AtlasTestHost or AtlasHttpSite",
        [
            "AccessAudit/LegacyAuditFixture.cs", "Ai/AiClientFactoryTests.cs", "HttpHostingTests.cs", "OidcTests.cs", "Platform/AtlasTestHost.cs",
            "Platform/MessageNamespaceConventionTests.cs", "Platform/PlatformTests.cs",
        ]),
        // A host and database of the test's own instead of the shared ones. Add a file only with the reason at the call.
        (IsolatedHost(), "a host of its own (CreateIsolatedAsync): the shared host serves tests that keep to their own users and spaces",
        [
            "AccessAudit/ToolAuditPinTests.cs", "Ai/AiAccessHttpTests.cs", "Ai/AiAdminHttpTests.cs", "AiIndex/AiIndexHttpTests.cs",
            "ApiActivity/ApiActivityTests.cs", "AppLogs/AppLogTests.cs", "Assistant/ConversationHttpTests.cs", "Attachments/AttachmentFixture.cs",
            "Auth/AuthPersistenceTests.cs", "Auth/ServerAccessTokenTests.cs", "Auth/UserAiPreferenceTests.cs", "Backups/BackupHttpTests.cs",
            "Backups/GitBackupTests.cs", "Backups/InstallationBackupHttpTests.cs", "Backups/InstallationFixture.cs",
            "Collaboration/CollaborationFixture.cs", "ExternalSharing/ExternalSharingHandlerTests.cs", "Hub/HubAuditTests.cs",
            "Hub/HubEffectiveAuthorityTests.cs", "Hub/HubMembershipPersistenceTests.cs", "Hub/HubSyncTests.cs", "Linking/SpaceLexiconHttpTests.cs",
            "Mcp/McpModuleStateTests.cs", "Mcp/McpSite.cs", "PageAccess/PageAccessManagementTests.cs", "PageGate/PageGateFixture.cs",
            "PageOrganization/PageSubtreeTests.cs", "PersonalApi/ApiTokenRightsTests.cs", "PersonalLibrary/PersonalLibraryFixture.cs",
            "Platform/AtlasHttpSite.cs", "Platform/AtlasTestHost.cs", "Scale/ScaleListTests.cs", "Search/SearchFixture.cs",
            "Security/SecurityFixture.cs", "Security/SecurityHttpTests.cs", "Security/SecuritySettingsAuditTests.cs", "Spaces/SpacePinTests.cs",
            "Spaces/SpacesHandlerTests.cs", "StorageProfiles/StorageProfileServiceTests.cs", "TrashCleanup/TrashCleanupHttpTests.cs",
            "Vaults/VaultFixture.cs", "YouTrack/YouTrackFixture.cs", "YouTrackHttpTests.cs",
        ]),
        // Besides the files named *Migration* or *Upgrade*: tests that stop at an earlier migration or start an installation
        // from nothing, written before this test (2026-11-13). Do not add to the list: a test of behaviour takes a migrated schema.
        (EmptyDatabase(), "an empty database: use DisposableSchema.CreateMigratedAsync, AtlasTestHost.CreateAsync(prefix) or AtlasHttpSite.CreateAsync",
        [
            "AccessAudit/AccessSqlParityTests.cs", "AccessAudit/LegacyAuditFixture.cs", "Administration/GrantValidityTests.cs", "AppLogs/AppLogTests.cs",
            "Auth/AppearanceCollaboratorColorTests.cs", "Auth/AppearanceDragPagesTests.cs", "Auth/AppearanceLanguageTests.cs", "Auth/AppearanceOpenLockedTests.cs", "Backups/BackupImportTests.cs",
            "Backups/InstallationBackupTests.cs", "Collaboration/CollaborationPendingTotalsTests.cs", "Collaboration/LiveSeedsTests.cs",
            "Contacts/AccountContactTests.cs", "Contacts/ContactDirectoryTests.cs", "Contacts/ContactLinkRunTests.cs", "Contacts/ContactPhotoTests.cs",
            "Conventions/SchemaWithoutLogicTests.cs", "Entities/EntityCatalogTests.cs", "Events/OutboxTests.cs", "Jobs/JobGateATests.cs",
            "Jobs/JobRuntimeTests.cs", "Linking/ContentReferenceTests.cs", "PageOrganization/TitleOrderTests.cs", "Pages/PublicationModeTests.cs",
            "Pages/RequirementSystemsJobTests.cs", "Pages/RequirementsHeadingsFollowUpTests.cs", "Pages/RequirementsHeadingsJobTests.cs",
            "Pages/RequirementsOverviewJobTests.cs", "Pages/RequirementsRepairOnOpenTests.cs", "Pages/RevisionVersionsTests.cs",
            "Platform/AtlasHttpSite.cs", "Platform/DatabaseStatisticsTests.cs", "Platform/DisposableSchema.cs", "Platform/OperationalRetentionTests.cs",
            "Platform/PlatformTests.cs", "Platform/TestDatabases.cs", "Relations/PageDerivedRowsTests.cs", "Relations/TemplateOriginTests.cs",
            "SearchPostgresTests.cs", "Security/SecuritySettingsAuditTests.cs", "Spaces/SpaceTrashTests.cs", "YouTrack/YouTrackHandlerTests.cs",
        ]),
    ];

    [Fact]
    public void Databases_and_hosts_come_from_the_shared_fixtures()
    {
        var files = Directory.EnumerateFiles(TestsDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && Path.GetFileName(path) != nameof(TestFixtureCostConventionTests) + ".cs")
            .Select(path => (Path: Path.GetRelativePath(TestsDirectory(), path).Replace('\\', '/'), Text: File.ReadAllText(path))).ToArray();
        var problems = new List<string>();
        foreach (var (pattern, what, allowed) in Reserved)
        {
            var found = files.Where(file => pattern.IsMatch(file.Text)).Select(file => file.Path).ToArray();
            Assert.True(found.Length > 0, "The scan no longer sees " + what);
            var exempt = pattern == EmptyDatabase() ? found.Where(path => path.Contains("Migration", StringComparison.Ordinal) || path.Contains("Upgrade", StringComparison.Ordinal)).ToArray() : [];
            problems.AddRange(found.Except(allowed).Except(exempt).Select(path => $"{path} has {what} (AGENTS.md, Testing, \"How a test gets its database\")"));
            problems.AddRange(allowed.Except(found).Select(path => $"{path} no longer has {what.Split(':')[0]}: remove it from the list"));
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [GeneratedRegex(@"\b(CREATE|DROP) DATABASE\b")]
    private static partial Regex DatabaseStatement();

    [GeneratedRegex(@"WebApplicationFactory<")]
    private static partial Regex WebFactory();

    [GeneratedRegex(@"Host\.CreateDefaultBuilder|Host\.CreateApplicationBuilder|WebApplication\.Create|new HostBuilder|new WebHostBuilder")]
    private static partial Regex HostBuilder();

    [GeneratedRegex(@"CreateIsolatedAsync")]
    private static partial Regex IsolatedHost();

    [GeneratedRegex(@"DisposableSchema\.CreateAsync|CreateEmptyAsync")]
    private static partial Regex EmptyDatabase();

    private static string TestsDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(Path.GetDirectoryName(path))!;
}
