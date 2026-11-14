# Tests that keep a host and database of their own (generated 2026-11-14, after the audit sweep)

Every call below takes `CreateIsolatedAsync`, `isolated: true`, `privateBecause:` or `own: true`. The reason is the comment at the call.
Not listed: fixtures that stay private without such a marker because they register services of one test (IndexFixture,
AiSettingsFixture, the job hosts, outbox hosts, backup sources and restore targets); their reasons are in the fixture.

| File:line | Reason at the call |
|---|---|
| AccessAudit/SecurityAuditPinTests.cs:97 | Own database: AuditPin compares every audit row and location revision written after its mark, not this test's only. |
| AccessAudit/ToolAuditPinTests.cs:20 | own database: flips the MCP switch and the AI access policies (one row each for the installation) and pins the whole audit log after a mark */; |
| Ai/AiAccessHttpTests.cs:13 | Own site: saves the installation's AI access list (GroupAiPolicy, one list with one version) and asserts it starts empty. |
| Ai/AiAdminHttpTests.cs:32 | Own site: saves the installation's chat provider (AiProfileAssignment/AiProfileRevision, one per kind) from no version. |
| AiIndex/AiIndexHttpTests.cs:14 | own database: saves the installation-wide embeddings profile row and the AI switch, and builds the one index generation |
| ApiActivity/ApiActivityTests.cs:199 | own database: McpSetting is one temporal row (the module switch) for the whole installation; the test asserts its history and its single current row */; |
| AppLogs/AppLogTests.cs:116 | own database: seeds the installation's one Hub connection (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and reads the whole log (its summary counts every line of AppLog) */; |
| Assistant/AnswerRerankTests.cs:97 | isolated: saves the rerank profile (AiProfile, one per installation) |
| Assistant/AssistantAgentTests.cs:325 | isolated: saves the chat profile (AiProfile, one per installation) and the task assistant.working-text (AiFeature) |
| Assistant/AssistantAgentTests.cs:407 | isolated: switches the task assistant.working-text (AiFeature, one per installation) |
| Assistant/AssistantPublishScopeProposalTests.cs:76 | Isolated: switches the task assistant.arrange-pages (AiFeature, one per installation). |
| Assistant/ConversationHttpTests.cs:16 | an installation without AI: the AI switch and profiles are rows of the whole installation, and other tests on the shared site configure them |
| Assistant/ConversationQuotaTests.cs:71 | Isolated: CancelStaleAiTurns cancels every stale turn of the whole AiMessage table and counts them. |
| Attachments/AttachmentFixture.cs:40 | (reason in the comment of the fixture or class) |
| Auth/AuthPersistenceTests.cs:41 | own database: creates the Workspace row (Id 1, one for the installation) that other code reads */; |
| Auth/AuthPersistenceTests.cs:53 | own database: ServerRecoveryToken allows one unconsumed row for the whole installation (UX_ServerRecoveryToken_Current) */; |
| Auth/AuthPersistenceTests.cs:74 | own database: needs an installation without a Workspace row (empty installation) */; |
| Auth/AuthPersistenceTests.cs:87 | own database: needs an installation without a Workspace row (empty installation) */; |
| Auth/ServerAccessTokenTests.cs:229 | Own database: the first start of an empty installation (bootstrap token, CompleteSetup) and its one current recovery token (UX_ServerRecoveryToken_Current); the recovery picks the account among all of them. |
| Auth/UserAiPreferenceTests.cs:19 | Own database: the test switches editor assist off for the whole installation (the current EditorAssistSetting row), which every GetCurrentUser on a shared host would then report (CurrentUserQueryTests failed beside it). |
| Backups/BackupHttpTests.cs:47 | own database: asserts the unconfigured backup settings and saves them (one row for the installation) |
| Backups/BackupHttpTests.cs:85 | own database: backup settings, GitBackupSettings and BackupDestinationChange are installation-wide rows counted by the test |
| Backups/BackupHttpTests.cs:118 | own database: backup settings and the backup key are installation-wide; the audit view is read whole |
| Backups/BackupHttpTests.cs:169 | own database: backup settings (one row) and the destination-change table read as "the last row" |
| Backups/GitBackupTests.cs:348 | (reason in the comment of the fixture or class) |
| Backups/InstallationBackupHttpTests.cs:16 | own database: issues the installation-wide backup key and saves backup settings |
| Backups/InstallationBackupHttpTests.cs:36 | own database: a restore replaces the whole installation and needs a new, empty one |
| Backups/InstallationBackupHttpTests.cs:59 | own database: saves the installation-wide backup settings |
| Backups/InstallationBackupHttpTests.cs:84 | own database: a restore replaces the whole installation and needs a new, empty one |
| Backups/InstallationFixture.cs:64 | (reason in the comment of the fixture or class) |
| CollaborationStoreTests.cs:133 | installs a failing trigger */; |
| ContentImport/ContentImportHandlerTests.cs:203 | Own host: the test creates a trigger on "PageRevision" (DDL) that fails every insert of the database. |
| ExternalSharing/PageAccessEventTests.cs:43 | (reason in the comment of the fixture or class) |
| ExternalSharing/PageAccessEventTests.cs:173 | (reason in the comment of the fixture or class) |
| Hub/HubAuditTests.cs:19 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Hub/HubAuditTests.cs:50 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Hub/HubAuditTests.cs:91 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Hub/HubEffectiveAuthorityTests.cs:87 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Hub/HubEffectiveAuthorityTests.cs:106 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Hub/HubEffectiveAuthorityTests.cs:127 | Own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton). |
| Hub/HubEffectiveAuthorityTests.cs:153 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Hub/HubEffectiveAuthorityTests.cs:169 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Hub/HubEffectiveAuthorityTests.cs:210 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Hub/HubEffectiveAuthorityTests.cs:240 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Hub/HubMembershipPersistenceTests.cs:15 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Hub/HubMembershipPersistenceTests.cs:33 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Hub/HubMembershipPersistenceTests.cs:62 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Hub/HubSyncTests.cs:13 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Hub/HubSyncTests.cs:29 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Hub/HubSyncTests.cs:58 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Hub/HubSyncTests.cs:91 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Hub/HubSyncTests.cs:111 | own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */; |
| Linking/SpaceLexiconHttpTests.cs:100 | own database: inserts Cap+1 wiki contacts, and every lexicon of the installation lists the wiki's contacts up to that whole-wiki cap */; |
| Mcp/McpAuthenticationTests.cs:168 | Off after installation and upgrade (O2): nothing reaches /mcp until a WikiAdmin enables it. Isolated: it switches the MCP module (one row, McpSetting, read by every /mcp request of the installation) off and on. |
| Mcp/McpModuleStateTests.cs:14 | own database: DDL (renames McpSetting, the installation's one module switch, and puts a view in its place) */; |
| Mcp/McpReadToolTests.cs:329 | isolated: the embeddings profile, AI switch and index generation are one setting of the installation, and FakeAiClientFactory.Meaning one fake |
| Mcp/McpReadToolTests.cs:368 | isolated: as above (embeddings profile, AI switch, index generation, one fake) |
| ModuleSettings/EditorAssistSettingsTests.cs:20 | switches the module of the installation (EditorAssistSetting) |
| ModuleSettings/ModuleSettingsHandlerTests.cs:20 | (reason in the comment of the fixture or class) |
| ModuleSettings/ModuleSettingsHandlerTests.cs:208 | (reason in the comment of the fixture or class) |
| ModuleSettings/ModuleSettingsHandlerTests.cs:243 | completes the module setup of the installation (ModuleOnboarding) |
| ModuleSettings/ModuleSettingsPersistenceTests.cs:11 | closes and replaces the one current sign-in settings revision of the installation (OidcSettingsRevision) |
| PageAccess/PageAccessManagementTests.cs:21 | (reason in the comment of the fixture or class) |
| PageAccess/PageAccessManagementTests.cs:104 | (reason in the comment of the fixture or class) |
| PageGate/PageGateLockTraceTests.cs:246 | Own host: turns the AI switch on (the current AiSetting row, one for the whole installation; a second save without its version is a 409). |
| PageOrganization/PageSubtreeTests.cs:45 | 70,000 pages in the shared database would make every whole-wiki RefreshPageDerivedAsync of other tests walk them in one transaction (out of shared memory) */; |
| PageOrganization/PageSubtreeTests.cs:59 | 70,000 pages in the shared database would make every whole-wiki RefreshPageDerivedAsync of other tests walk them in one transaction (out of shared memory) */; |
| PersonalApiHttpTests.cs:191 | (reason in the comment of the fixture or class) |
| PersonalLibrary/PersonalLibraryHandlerTests.cs:43 | own database: runs the retention sweep, which trims and ages every operational log of the installation (job runs, notifications, page views) */; |
| Projector/ProjectorMcpToolTests.cs:42 | Isolated: the fixture installs a trigger on "Page" (DDL), which would add backup rows for every page of other tests. |
| Scale/ScaleListTests.cs:46 | own database: seeds and measures a whole installation (counts every account, clears the index queue, ANALYZE, sweeps every page) */; |
| Search/SearchLatencyTests.cs:80 | Own database: the budget counts calls of the trigram operator over every published row the query's plan reaches before the access filter, so pages of other tests would be counted too. |
| Search/SearchWithoutDatabaseLogicMigrationTests.cs:92 | the rebuild job rewrites the search documents of every page of the installation and deadlocks with tests writing pages beside it |
| Security/SecurityFixture.cs:68 | Own database: the test changes or counts the installation's security configuration (see shared). |
| Security/SecurityHttpTests.cs:38 | Own site: imports Http:TrustedProxies and changes the installation's trusted networks (TrustedAddress) and instance protection (SecurityConfiguration row Id 1). |
| Security/SecuritySettingsAuditTests.cs:49 | own database: the backup key is one row (BackupSettings) for the whole installation; the test starts without one */; |
| Security/SecuritySettingsAuditTests.cs:70 | own database: it rewrites GitBackupSettings and BackupSettings, one row each for the whole installation */; |
| Spaces/SpacePinTests.cs:56 | the wiki administrator's list holds the first 200 spaces of the whole installation by name: on the shared host this test's spaces can fall beyond it |
| Spaces/SpacesHandlerTests.cs:13 | the wiki administrator's list holds the first 200 spaces of the whole installation by name: on the shared host this test's spaces can fall beyond it |
| StorageProfiles/StorageProfileServiceTests.cs:105 | own database: sets BackupSettings (the one row for the installation, id 1, read by the backup code) to a profile */; |
| TrashCleanup/TrashCleanupHttpTests.cs:19 | own database: saves the wiki-wide cleanup rule (one row of the installation) that locks the rule of every space */; |
| TrashCleanup/TrashCleanupHttpTests.cs:61 | own database: runs the daily cleanup (TrashCleanupRunner.RunDueAsync), a job over every space and the wiki rule of the installation, and asserts its total */; |
| Vaults/VaultDerivedKeyTests.cs:64 | Own host: KdbxKeyDerivation.Derivations counts every derivation of the host, so other tests' attaches and releases would count. |
| Vaults/VaultDerivedKeyTests.cs:104 | Own host: KdbxKeyDerivation.Derivations counts every derivation of the host, so other tests' attaches and releases would count. |
| Vaults/VaultReleaseTests.cs:166 | Own database: the test adds a trigger to SecretReleaseEvent (DDL) and counts every value the host's watch holds. |
| YouTrackHttpTests.cs:25 | (reason in the comment of the fixture or class) |
| YouTrackHttpTests.cs:63 | (reason in the comment of the fixture or class) |
| YouTrackHttpTests.cs:102 | (reason in the comment of the fixture or class) |
| YouTrackHttpTests.cs:136 | (reason in the comment of the fixture or class) |
| YouTrackHttpTests.cs:167 | (reason in the comment of the fixture or class) |
| YouTrackHttpTests.cs:195 | (reason in the comment of the fixture or class) |
| YouTrackHttpTests.cs:235 | (reason in the comment of the fixture or class) |
| YouTrackHttpTests.cs:266 | (reason in the comment of the fixture or class) |
| YouTrackHttpTests.cs:310 | (reason in the comment of the fixture or class) |
| YouTrackHttpTests.cs:340 | (reason in the comment of the fixture or class) |
