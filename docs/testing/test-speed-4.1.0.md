# Test suite speed, 4.1.0 (measurements)

Numbers from one workstation, taken under these limits: the test process pinned to 8 logical processors at
BelowNormal priority, one test process at a time, the database container (`atlas-postgres:18.6-pgvector0.8.7`, default
settings as the CI service: fsync on, max_connections 100) limited to 8 CPUs. Before and after are measured the same way.
The batches are those of `tooling/ci/server-tests.sh`.

## Baseline (with cost counters), server suite as CI batches

```
AB exit=0 wall=305s Passed!  - Failed:     0, Passed:   813, Skipped:     5, Total:   818, Duration: 5 m 2 s - Atlas.Server.Tests.dll (net10.0)
CD exit=0 wall=145s Passed!  - Failed:     0, Passed:   524, Skipped:     0, Total:   524, Duration: 2 m 21 s - Atlas.Server.Tests.dll (net10.0)
EFGHIJKLM exit=0 wall=290s Passed!  - Failed:     0, Passed:   438, Skipped:     0, Total:   438, Duration: 4 m 46 s - Atlas.Server.Tests.dll (net10.0)
NO exit=0 wall=55s Passed!  - Failed:     0, Passed:    64, Skipped:     0, Total:    64, Duration: 52 s - Atlas.Server.Tests.dll (net10.0)
P exit=0 wall=271s Passed!  - Failed:     0, Passed:   752, Skipped:     3, Total:   755, Duration: 4 m 28 s - Atlas.Server.Tests.dll (net10.0)
QRS exit=0 wall=265s Passed!  - Failed:     0, Passed:   504, Skipped:     1, Total:   505, Duration: 4 m 22 s - Atlas.Server.Tests.dll (net10.0)
rest exit=0 wall=69s Passed!  - Failed:     0, Passed:   201, Skipped:     0, Total:   201, Duration: 1 m 7 s - Atlas.Server.Tests.dll (net10.0)

Fixture costs per process (time summed over parallel tests; the lines of AB and EFGHIJKLM were not written):
pid 13160 wall 140s: database clone 126x 25,0s, database drop 148x 338,1s, database empty 15x 1,5s, host build 95x 1,1s, host start 95x 18,0s, host stop 95x 0,4s, site start 38x 65,3s, site stop 38x 2,9s, template migrate 1x 3,5s
pid 63144 wall 286s: database clone 250x 40,4s, database drop 304x 722,1s, database empty 48x 3,9s, host build 160x 1,6s, host start 160x 22,3s, host stop 160x 1,1s, site start 99x 180,0s, site stop 99x 1,2s, template migrate 1x 3,1s
pid 22796 wall 53s: database clone 40x 4,2s, database drop 42x 50,5s, database empty 3x 0,2s, host build 22x 0,2s, host start 22x 3,6s, host stop 22x 0,2s, site start 5x 7,2s, site stop 5x 0,0s, template migrate 1x 2,8s
pid 62656 wall 269s: database clone 412x 73,4s, database drop 503x 897,9s, database empty 58x 6,7s, host build 358x 4,0s, host start 358x 53,2s, host stop 358x 1,5s, site start 80x 138,1s, site stop 80x 4,8s, template migrate 1x 2,7s
pid 24688 wall 262s: database clone 178x 30,6s, database drop 203x 325,1s, database empty 24x 2,9s, host build 151x 1,6s, host start 151x 20,6s, host stop 151x 0,6s, site start 25x 34,5s, site stop 25x 2,2s, template migrate 1x 2,7s
pid 23856 wall 67s: database clone 72x 11,0s, database drop 76x 100,5s, database empty 4x 0,7s, host build 55x 0,7s, host start 55x 9,1s, host stop 55x 0,2s, site start 18x 20,5s, site stop 18x 0,3s, template migrate 1x 2,8s

tests 3305 sum of test durations 8587s
-- slowest classes (sum of its tests, s; tests)
  173.8   36 FluentMigrationTests
  145.0    1 Search.Parity.SearchParityTests
  125.9   16 Backups.InstallationBackupTests
  121.6    9 Backups.GitBackupTests
  104.5   11 Mcp.McpDraftToolTests
   99.8   21 Boards.BoardHandlerTests
   94.8   23 SearchQueryPostgresTests
   92.1   13 Projector.ProjectorMcpToolTests
   81.0    5 Live.AdminFeedRightsTests
   79.2    8 Assistant.AssistantPublishScopeProposalTests
   76.8    6 Backups.InstallationBackupGitTests
   74.8   18 Jobs.JobRuntimeTests
   73.2    9 ContentImport.ContentImportHandlerTests
   69.2    8 Assistant.AssistantAgentTests
   68.8    3 Mcp.McpOverviewToolTests
   66.9   15 Assistant.AnswerFlowTests
   66.3   11 Mcp.McpReadToolTests
   66.1   13 Jobs.JobGateATests
   65.1   14 PageHistoryTests
   64.3   13 Events.OutboxTests
   63.4    4 Mcp.McpPublishTests
   63.4    5 ExternalSharing.ExternalSharingHttpTests
   62.4   12 Administration.GrantValidityTests
   55.3    6 Access.AccessHttpTests
   54.7    5 DelayedAuthorizationTests
   53.1    9 Assistant.AnswerSourcesTests
   52.7   21 OidcTests
   51.3    9 AiIndex.IndexRunnerTests
   51.2    3 ModuleSettingsHttpTests
   50.2    5 Backups.BackupHttpTests
-- slowest tests
  145.0 Search.Parity.SearchParityTests.Every_read_function_and_its_query_text_return_the_same_rows
   45.1 ContentExport.ContentPdfRendererTests.A_print_that_overruns_its_deadline_names_the_step_and_leaves_no_browse
   44.3 Boards.BoardHandlerTests.A_batch_that_would_make_the_board_larger_than_its_budget_is_refused
   39.4 Live.AdminFeedRightsTests.Ai_capabilities_reach_every_account_and_ai_health_only_the_wiki_admini
   35.8 Collaboration.CollaborationJoinBudgetTests.Joining_a_seeded_page_authorizes_once_and_stays_within_its_sql_budget
   35.5 Security.SecurityHttpTests.Security_api_follows_the_imported_proxies
   35.1 Mcp.McpPublishTests.Unsaved_live_edits_block_publication
   35.1 ModuleSettingsHttpTests.Editor_assist_switch_is_exposed_as_features_on_the_current_user
   35.1 Mcp.McpDraftToolTests.A_section_writes_contacts_and_charts_in_the_dialect_and_an_invalid_one
   35.1 Mcp.McpOverviewToolTests.Mentions_find_czech_word_forms_and_keep_short_words_and_phrases_exact
   35.0 Mcp.McpRequestLimitTests.Largest_upload_passes_a_real_server_and_a_larger_request_is_refused
   35.0 KnowledgeMaintenance.AiReportsTests.Usage_attributes_historical_providers_and_only_fully_known_costs_to_ut
   35.0 ExternalSharing.ExternalSharingHttpTests.Public_OpenAPI_page_of_512_KiB_is_accepted_and_shared_as_reference
   33.8 Objects.BoardSourcesLiveHttpTests.Board_viewers_hear_their_sources_renamed_trashed_restored_and_purged
   33.5 Notifications.NotificationHttpTests.Every_route_and_tool_answers_each_actor_as_its_rights_say_and_nothing_
```

## After (same machine, same limits; before the move to .NET 11)

| Server batch | Baseline | Pool, fsync on | fsync off, no pool (kept) | fsync off + pool |
|---|---|---|---|---|
| AB | 305 s | 232 s | 170 s | 150 s |
| CD | 145 s | 104 s | 69 s | 60 s |
| EFGHIJKLM | 290 s | 246 s | 178 s | 178 s |
| NO | 55 s | 25 s | 25 s | 28 s |
| P | 271 s | 233 s | 124 s | 132 s |
| QRS | 265 s | 189 s | 164 s | 185 s |
| remaining | 69 s | 48 s | 34 s | 40 s |
| **sum** | **1400 s** | **1077 s** | **764 s** | **773 s** |

All runs green (3305 tests). One process over the whole suite, fsync off: 885 s, 3297 passed, 9 skipped, no failed
connection, peak memory 22.8 GB; 1705 databases (1533 clones, 172 empty) and 1562 hosts (1211 AtlasTestHost, 351 AtlasHttpSite).

- **fsync** carried the time: clone 190 ms -> 32 ms, drop 2.3 s -> 100 ms, and every commit of every test.
- **Database pool rejected**: a reset that leaves the database as a new clone truncates all 159 tables (0.5 s); a DELETE reset
  broke two buffer-budget tests (ContentsBudgetTests, SearchLatencyTests). The patch is not in the branch.
- **Handler cache**: the first tests of a process waited 17-22 s for 500 Roslyn compiles, about 6 s from the cache.
- **Web vitest** (8 workers): 165 s -> 144 s (127 files in node instead of jsdom). Editor: 59 s, nothing cheap left
  (interaction.contract 49 s, schema/manifest 31 s set the wall time).

## Left slow, and why

- One process is slower than batches and needs 22.8 GB: every AtlasHttpSite a process started stays in memory and each new
  one starts slower (1.5 s -> 6 s). Roots seen in heap dumps: the MCP SDK's tool descriptor cache on its static JSON options
  (key holds the host's services), the rate limiters' timer loops capturing the start context, Npgsql's per-connection-string
  pools. Two attempted fixes did not free the sites and were reverted. Until this is fixed the batches are the memory control.
- AtlasHttpSite start: about 1 s CPU each compiling ~450 minimal API endpoints (RequestDelegateFactory, Expression.Compile).
  The request delegate generator would remove it; a production build change, not tried.
- Batch wall time is set by the longest serial class: FluentMigrationTests (36 tests, 110-174 s), SearchParityTests (one test,
  150 s), InstallationBackupTests. Merging EFGHIJKLM with QRS would overlap them; not done, per-batch memory not measured.
- Fixed sleeps are not a cost (7 of 1-1.5 s in the whole server suite).
- Playwright (not in CI, not run here): first-run.spec.ts repeats in each of the 6 pods (about 42 s each),
  storage-export.spec.ts:182 waits a fixed 60 s.

## After the move to .NET 11 (WolverineFx 6.64.0): targeted proof only

On net11.0, against a new `atlas-postgres` container with default settings (fsync on) and `ATLAS_TEST_DISPOSABLE_SERVER=1`:
the guards (`Category=Guard`, with `TestFixtureCostConventionTests`), batch NO, `SharedHandlerCompilerTests` and
`WolverineScheduledIndexMigrationTests`: 114 tests, passed twice (58 s, then 23 s), the run switched fsync off itself
(`show fsync` = off afterwards). Without the variable the same server is refused with the message that names the fix.
Batch NO alone, before the merge: 55 s -> 25 s.

Estimate for the whole suite (an estimate, from the runs before the merge): CI server batches about 45 % shorter
(1400 s -> 764 s here; the CI runner's disk decides the real share), web vitest about 13 % shorter.

## Commands for the combined run

```bash
# database: the production image with the recipe's settings (or default settings plus ATLAS_TEST_DISPOSABLE_SERVER=1)
podman run -d --name atlas-test-pg-prod -p 127.0.0.1:15470:5432 -e POSTGRES_PASSWORD=atlas -e POSTGRES_DB=atlas_test --tmpfs /var/lib/postgresql:rw,size=8g localhost/atlas-postgres:18.6-pgvector0.8.7 -c max_connections=500 -c fsync=off -c full_page_writes=off -c shared_buffers=512MB
export ATLAS_TEST_CONNECTION='Host=127.0.0.1;Port=15470;Username=postgres;Password=atlas;Database=atlas_test'
export ATLAS_TEST_DATABASE="$ATLAS_TEST_CONNECTION"
export ATLAS_TEST_COSTS="$PWD/artifacts/server-costs"      # optional: one line per test process in server-costs.<pid>
dotnet build tests/Atlas.Server.Tests -c Release
# batches, as CI (prints the duration of every part and the fixture costs; RUNNER_TEMP needs NuGet.Config as in the workflow)
RUNNER_TEMP=<dir> bash tooling/ci/server-tests.sh
# or by hand, one batch
dotnet test tests/Atlas.Server.Tests --no-build -c Release --filter "FullyQualifiedName~Atlas.Server.Tests.N|FullyQualifiedName~Atlas.Server.Tests.O"
# one process over the whole suite (needs about 23 GB of memory at 8 threads, more with more cores)
dotnet test tests/Atlas.Server.Tests --no-build -c Release
# web and editor
pnpm --dir src/Atlas.Web test
pnpm --dir src/Atlas.Web --filter @atlas/markdown exec vitest --configLoader runner --run
```

The existing shared container `atlas-test-pg-prod` (fsync on) is refused until it is started again as above or the run
gets `ATLAS_TEST_DISPOSABLE_SERVER=1`.

No test changed its isolation model in kind: every fixture still gets its own clone of the template. To watch in the
combined run: the 127 web test files that now run in node (a new DOM use in one of them fails with "window is not defined").

## Phase 2 (2026-11-13 evening): the 35-second cluster, host memory, the giants

Proven on single classes only (a container of 4 CPUs, fsync off, 8 processors, BelowNormal).

**The 35-second cluster is the start of a test process, not a timeout.** In the baseline 29 tests took 30 s or more; 26 of
them started in the first second of their batch (the `startTime` of the trx): one per xunit thread, each waiting for what a
process does once (compile about 500 handlers 17-22 s, migrate the template, the first hosts). After the handler cache and
fsync off, 3 tests of 30 s or more are left, each long by its own work: `SearchParityTests` (136 s, now 46 s),
`ContentPdfRendererTests.A_print_that_overruns_its_deadline…` (45 s) and `BoardHandlerTests.A_batch_that_would_make_the_board_larger…` (42 s).

**SearchParityTests**: about 150 s of comparisons, each on its own connection and transaction, ran one after another. Up to
eight queries run at once now, same cases and assertions: 153 s -> 46 s alone under the same conditions.

**Host memory: not solved.** A loop of 16 hosts in one process, managed memory after a full collection (net11.0):

| what each iteration does | kept per host | time each |
|---|---|---|
| AtlasTestHost, one seeded row | 6 MB | 1.6 s |
| AtlasHttpSite, started only | 19 MB | 1.2 s |
| AtlasHttpSite, one request | 24 MB | 2.1 s |
| AtlasHttpSite, setup and a signed-in request | 25 MB | 2.5 s |

So the memory stays after the start alone, in both kinds of host (1211 x 6 MB + 351 x 25 MB is about 16 GB of the 22.8 GB).
Three real roots were found in heap dumps and removed in a patch that is NOT in the branch, because the loop measured the
same 25 MB per site with it (the patch was not kept):
the MCP SDK's tool cache on its static JSON options (a per-host copy), the rate limiting middleware's limiter that ASP.NET
never disposes (disposed from the fixture), and the two limiters of `PersonalApiConcurrency.Limiter()` behind the global
limiter (a chained limiter does not dispose its parts; a real gap in the product, harmless with one host per process).
After them `gcroot` of dotnet-dump 9 reports no root for the hosts although the collector keeps them, and it cannot read a
.NET 11 dump at all. Next step: a current dotnet-gcdump or dotnet-dump (not installed here: a download), then the roots of
`Wolverine.Runtime.WolverineRuntime` after a 16-host loop. Sharing one host per class is not started.

**FluentMigrationTests**: 36 tests, 2.8 s each, 101 s one after another (xunit runs the tests of a class in turn); each
migrates an empty database to head. Two proposals, neither mechanical, neither done: split the class into three by feature
(the classes then run side by side: about 35 s of wall time), or clone the state before the migration under test from a
database migrated once per version (saves the prefix, about half of each test).

**Batches**: A and B apart, N and O with the remainder. Test time per first letter at 8 threads (sum of test durations, s):
A 644, B 430, C 364, E 120, F 101, J 91, L 95, M 404, O 84, P 729, R 64, S 406 (316 after the parity change), V 91, the rest under 50.
Estimate, not measured: batches about 675 s under the limits above (was 764 s), the largest batch about 3.5 GB instead of 5-6 GB.
Peak memory per batch is printed by the cost line of each process; it was not measured after the change.

## Phase 3 (2026-11-13 night): one server and one database, tests on their own data (in progress)

The target: one test server and one database per test process, not one per test; exceptions only for the kind of
"upgrade from a previous version" (a schema version, the first start, DDL, a row the whole installation shares); tests that
do not earn their cost are deleted while converting.

- `AtlasTestHost.CreateAsync(prefix)` and `AtlasHttpSite.CreateAsync(prefix)` without a configuration return the shared host /
  site; `AtlasHttpSite.CreateAsync(prefix, settings, shared: true)` shares one site per settings. `CreateIsolatedAsync` is the
  greppable exception and the guard lists its files. On a shared site `CompleteSetupAsync` gives the test its own wiki
  administrator, `OwnSpaceAsync` its own space, and every client its own address (rate limits are per address).
- Flipping the default without converting anything (8 processors, batches one at a time, fsync off):
  handler host: 134 of 3308 tests failed (A 9, B 0, CD 33 before its fix, E-M 24, P 45, QRS 55, rest 1), in 40 classes;
  HTTP site on top: 148 failed in 63 classes. Causes, by count: fixed logins and names against unique indexes, whole-table
  counts and "the first space", one-row-per-installation settings (Hub connection, backup settings), a direct POST to
  /api/setup/complete, tests that install a trigger.
- With the shared site and nothing converted yet (the failing classes excluded), per batch before -> after:
  HTTP sites A 71 -> 20, E-M 99 -> 40, P 80 -> 30, QRS 25 -> 6; peak memory A 5.6 -> 3.2 GB, E-M 7.2 -> 3.6 GB, P 6.8 -> 3.9 GB.
- Conversion by area (each area proving its classes three times together): hub/auth/storage 20 tests converted, 21 kept
  isolated (the Hub connection is one row: `UX_HubConnectionRevision_Singleton`; the Workspace row; the recovery token; backup
  settings); pages 88 tests green, 2 kept isolated (70 000 pages starve `RefreshPageDerivedAsync` of everyone else);
  access/sharing 165 tests green, 2 fixtures kept isolated (a trigger on `PageAccessEvent`; role counts that include every wiki
  administrator of the installation). Search and the HTTP areas (MCP, assistant/AI/backups, access/admin, content) are running.
- A bug of this branch found on the way: the cost line's timer crashed the test host when its file was busy (fixed).

### State after merging seven conversion branches (2026-11-14, batches one at a time, 8 processors, fsync off)

| Batch | hosts built before (phase 2) | now | HTTP sites before | now | peak memory before | now | failing |
|---|---|---|---|---|---|---|---|
| A | 203 | 151 | 71 | 18 | 5.6 GB | 2.8 GB | 27 (classes behind McpSite, in conversion) |
| B | 134 | 121 | 22 | 19 | 2.6 GB | 2.7 GB | 0 |
| CD | 95 | 51 | 38 | 26 | 3.8 GB | 3.1 GB | 7 |
| EFGHIJKLM | 160 | 120 | 99 | 44 | 7.2 GB | 3.8 GB | 8 |
| P | 358 | 116 | 80 | 29 | 6.8 GB | 3.7 GB | 3 |
| QRS | 151 | 76 | 25 | 7 | 2.9 GB | 2.1 GB | 0 |
| remaining | 55 | 51 | 18 | 19 | 3.2 GB | 2.8 GB | 1 |

46 failing tests in all: 33 in the classes built on McpSite outside Mcp/ (in conversion), 13 others (Live admin
feed, AI admin HTTP, one each in Vaults, Projector rights matrix, page people) assigned. Private hosts still built, by schema
prefix: boards 152 (in conversion), AI index 118 and AI settings 26 (not shareable without a new fixture mechanism: the index
worker claims the whole task queue, the fake clock is the host's, tests change the installation's AI settings), MCP 56,
backups 32 + 19 restore targets (an archive is the whole installation), attachments 31, security 21, vaults 17.

### Final state of phase 3 (2026-11-14, every conversion branch merged; batches one at a time, 8 processors, fsync off)

| Batch | wall | tests | hosts built | HTTP sites | databases cloned | peak memory |
|---|---|---|---|---|---|---|
| A | 66 s | 476 passed | 152 | 31 | 182 | 3.6 GB |
| B | 90 s | 329 passed, 5 skipped | 90 | 15 | 104 | 2.4 GB |
| CD | 60 s | 525 passed | 31 | 26 | 50 | 3.3 GB |
| EFGHIJKLM | 124 s | 439 passed | 119 | 42 | 151 | 3.8 GB |
| P | 96 s | 751 passed, 3 skipped | 95 | 29 | 97 | 3.7 GB |
| QRS | 74 s | 504 passed, 1 skipped | 51 | 9 | 60 | 2.2 GB |
| remaining | 38 s | 265 passed | 30 | 19 | 60 | 3.1 GB |
| **all** | **548 s** | **3289 passed, 0 failed** | **568** | **171** | **704** | |

Same machine and limits: 1400 s at the start, 764 s after fsync, 548 s now; hosts 1211 -> 568, HTTP sites 351 -> 171,
databases 1705 -> 704; the largest batch 7.2 GB -> 3.8 GB. This is ONE pass: the three passes before it each had one to three
failures of interference that were then fixed (a cached MCP switch, a deadlock of a page purge beside the whole-database
refresh of derived rows, a test that needs an installation without AI); a repeated pass has not been run.

Private hosts still built, by schema prefix: AI index 118 and AI settings 26 (the index worker claims the whole task queue,
the fake clock is the host's, tests change the installation's AI settings: needs a new fixture mechanism), boards 64 (outbox
hosts, budgets, sweeps over every account), MCP 60 (fake services per test, one site per assistant class), backups 32 + 19
restore targets + 9 (an archive is the whole installation), attachments 31, security 16, PDF auto export 14, modules 12.
The calls that keep a host of their own with their reasons: `docs/testing/isolated-tests-4.1.0.md` (96 calls; 17 of them
have the reason in the fixture, not at the call).

Deleted while converting (each covered elsewhere, named in the conversion reports): 11 tests (AI 7, backups 1,
profile sign-in retention 1, the editor-assist switch over HTTP 1, a restore move 1) and the copied 401 / CSRF-400 assertions
of about twenty HTTP tests.

Weakened, to decide: "the folder is empty" checks are gone where the folder is shared (content export, two MCP attachment
tests, space covers); the rule "the last wiki administrator cannot be deactivated" is asserted in the handler tests only;
`SpaceTrashHttpTests` asserts its own contact is absent instead of `total == 0`; `JobGateATests` A7 measures the start of a
shared host for its hosts without durability; BoardLiveHttpTests and BoardSourcesLiveHttpTests fail together on a shared
site for a reason not found (the hub clients do not receive BoardChanged) and stay private.

### Audit sweep and acceptance (2026-11-14)

After four rounds of single interference failures the areas were audited one by one for the shapes that break on a shared
database (first item of an installation-wide list, `Assert.Single`/`Empty`/counts, unscoped SQL, fixed ids and names, switches
of the whole installation, capped administrator lists, clock and buffer budgets). The causes that carried the failures:

- A shared `AtlasHttpSite` was started by every test that reached it at once: `WebApplicationFactory` does not lock its start,
  17 hosts over one database were counted. The site is now started once, inside its lazy.
- `McpModuleStateTests` renamed the `McpSetting` table on the shared host (isolated now).
- `SearchFixture.Execute` appended the refresh of the derived rows of the whole database to every statement (per page now).
- Tests reading the wiki administrator's capped lists (200 spaces by name) or the first item of an installation-wide list
  work in their own space or keep a host of their own with the reason at the call.

Acceptance: three consecutive passes of all batches, one batch at a time, BelowNormal, 8 processors, database container with
4 CPUs and fsync off. Each pass: 3287 passed, 9 skipped, 0 failed.

| Pass | A | B | CD | EFGHIJKLM | P | QRS | remaining | all |
|---|---|---|---|---|---|---|---|---|
| c1 | 76 s | 138 s | 66 s | 160 s | 91 s | 76 s | 46 s | 653 s |
| c2 | 48 s | 96 s | 81 s | 122 s | 92 s | 95 s | 35 s | 569 s |
| c3 | 46 s | 96 s | 61 s | 138 s | 83 s | 134 s | 32 s | 590 s |

Per pass: 578 hosts built, 169 HTTP sites started, 717 databases cloned; the largest batch peaks at 3.7 GB. The walls move
with what else the machine does (other work ran during some batches).

Before these, passes a1 and a2 were green and two passes each lost one test with a wall-clock budget on a host and data of
its own while the machine was loaded by other work (every batch of those passes took two to three times as long):
`UsageBudgetTests.The_uses_of_the_most_used_contact_list_and_count_within_the_budget` (p95 362 ms against 150 ms, median
84 ms) and `ContactLinkBudgetTests.The_report_of_a_large_wiki_answers_within_the_budget` (2279 ms against 900 ms). Both
pass alone on the quiet machine. They are not interference; the budgets were left as they are.

The calls that keep a host of their own: `docs/testing/isolated-tests-4.1.0.md` (95 calls, 20 with the reason in the comment
of the fixture or class).

## Not verified

- No full run after the move to .NET 11, no repeated runs for intermittent failures,
  no rebalanced batches (per-batch peak memory is printed by the cost line now), tests/changed.ps1 and tests/smoke.ps1 not
  run with the new fsync check, server-tests.sh not run as a whole (bash -n only), web vitest not rerun on net11.
- Web vitest: PageAccessPanel.test.ts (2) and spacePages.test.ts (1) failed before and after, 5 of 5 runs alone.
