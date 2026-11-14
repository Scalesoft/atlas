<script lang="ts">
import { lazySettings } from "../lazySettings";

// The existing administration panels, each in its own chunk as before. Created once for the module, not per surface:
// defineAsyncComponent keeps the chunk it has loaded, so opening the settings again renders a visited page at once.
const AccountsPage = lazySettings(() => import("../people/AccountsPage.vue"));
const GroupsPage = lazySettings(() => import("../people/GroupsPage.vue"));
const AccessHistoryPage = lazySettings(() => import("./AccessHistoryPage.vue"));
const ProtectionPage = lazySettings(() => import("./ProtectionPage.vue"));
const NetworksPage = lazySettings(() => import("./NetworksPage.vue"));
const ApiTokensPage = lazySettings(() => import("../people/ApiTokensPage.vue"));
const SpacesAccessPage = lazySettings(() => import("./SpacesAccessPage.vue"));
const AccessKindPage = lazySettings(() => import("./AccessKindPage.vue"));
const AccessRequestsPage = lazySettings(() => import("../requests/AccessRequestsPage.vue"));
const ModulesPage = lazySettings(() => import("../general/ModulesPage.vue"));
const AuthenticationPage = lazySettings(() => import("../general/AuthenticationPage.vue"));
const StorageProfilesPage = lazySettings(() => import("./StorageProfilesPage.vue"));
const BackupsPage = lazySettings(() => import("../general/BackupsPage.vue"));
const SpaceTrashPage = lazySettings(() => import("./SpaceTrashPage.vue"));
const ContactLinkPage = lazySettings(() => import("./ContactLinkPage.vue"));
const JobsPage = lazySettings(() => import("../jobs/JobsPage.vue"));
const LogsPage = lazySettings(() => import("../logs/LogsPage.vue"));
</script>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, provide, ref, watch } from "vue";
import { useAiAdminHealth } from "../general/ai/useAiAdminHealth";
import AtlasIcon from "../../AtlasIcon.vue";
import AtlasVersionInfo from "../../AtlasVersionInfo.vue";
import { Button } from "../../ui/button";
import EmptyState from "../EmptyState.vue";
import ResultBanner from "../ResultBanner.vue";
import SettingsShell from "../SettingsShell.vue";
import { useSettingsContext } from "../context";
import { useSettingsFlash } from "../flash";
import { useSettingsRoute } from "../useSettingsRoute";
import { useVaultRegistry } from "../vaultRegistry";
import type { SettingsNavGroup, SettingsNavItem } from "../types";
import AddVaultPage from "../space/AddVaultPage.vue";
import AllVaultsPage from "./AllVaultsPage.vue";
import LocationsPage from "./LocationsPage.vue";
import OverviewPage from "./OverviewPage.vue";
import { settingsIssues, useJobsIssues, useLogsIssue, useOutboxIssue } from "./issues";
import { useAccessRequestFeed } from "../requests/accessRequestFeed";
import { useSecurityState } from "../security/securityState";
import { createSecurityOverview } from "../security/securityOverview";
import { pluginKinds, useAccessKinds } from "../access/accessKinds";
import { createDirectorySummary, directorySummaryKey } from "../people/directorySummary";
import { loadModules, moduleEntry, type ModuleDescriptor, type ModuleState } from "../general/moduleRegistry";
import { usePageSwap } from "../pageSwap";
import t from "../../../i18n/settings-global";
import { locale } from "../../../i18n";

/**
 * Správa wiki for the wiki administrator, the security centre (docs/access-model.md §4): Přehled and Audit · Lidé
 * (Lidé, Skupiny, Přihlašování) · Prostory (Prostory a přístupy, one item per plugin kind of the access registry) ·
 * Trezory a hesla (Trezory, Žádosti o přístup, Ochrana hesel) · AI a integrace (AI, one item per integration module,
 * API tokeny) · Provoz (Zálohy, Profily úložišť, Umístění souborů, Sítě a proxy, Import z Wiki.js).
 * Every page stays in its own chunk. Every wiki administrator manages global security.
 */
const context = useSettingsContext();
const aiHealth = useAiAdminHealth();
const aiIssues = computed(() => aiHealth.failures.value.length + (aiHealth.error.value ? 1 : 0));
const router = useSettingsRoute();
// The sections swap with the shared page hooks (pageSwap.ts): the new one starts at the top, the old one fades where the reader saw it.
const swap = usePageSwap(() => router.route.value.path);
const flash = useSettingsFlash();
const registry = useVaultRegistry();
const requests = useAccessRequestFeed();
const security = useSecurityState();
const overviewFacts = createSecurityOverview();
// The Prostory group names every enabled plugin kind; the list comes from GET /api/access/kinds.
useAccessKinds();
// Refresh 2.5 rail (boards 06–10): people and groups carry their counts, every integration module has its own
// item with its state, so nothing waits behind a "Moduly a integrace" card list.
const directory = createDirectorySummary();
provide(directorySummaryKey, directory);
// Dead background messages are an issue of Přehled with a retry in place (ARCH1-13).
const outbox = useOutboxIssue();
// So are the jobs whose newest run failed (Atlas 3.3), and the rail counts them.
const jobs = useJobsIssues();
const logs = useLogsIssue();
const modules = ref<ModuleDescriptor[]>();
const moduleStates = ref<Record<string, ModuleState>>({});
const modulesFailed = ref(false);
let disposed = false;
/** Authentication (OIDC, Hub), AI and the import have their own items; the rest are integrations. */
const sectionModules = ["oidc", "hub", "ai", "wiki-js-import"];
const integrationModules = computed(() => (modules.value ?? []).filter((item) => !sectionModules.includes(item.settingsUi)));
async function loadIntegrations() {
  if (!context.isWikiAdmin) return;
  try {
    const result = await loadModules();
    if (disposed) return;
    modules.value = result;
    for (const item of integrationModules.value) {
      moduleEntry(item.settingsUi)?.state?.()
        .then((state) => { if (!disposed) moduleStates.value = { ...moduleStates.value, [item.id]: state }; })
        .catch(() => undefined);
    }
  } catch {
    if (!disposed) modulesFailed.value = true;
  }
}
onMounted(() => {
  void overviewFacts.load();
  if (context.isWikiAdmin) void directory.load();
  if (context.isWikiAdmin) void outbox.load();
  if (context.isWikiAdmin) void jobs.load();
  if (context.isWikiAdmin) void logs.load();
  void loadIntegrations();
});
onBeforeUnmount(() => (disposed = true));
// Inviting a person or creating a group ends on another people page: refresh the rail counts and the tiles then.
watch(() => router.route.value.path, (path, previous) => {
  if (context.isWikiAdmin && path !== previous && /^(people\/|security\/overview$)/.test(path)) void directory.load();
  // Leaving the jobs (a run was started, retried or stopped there): the failed ones are counted again.
  if (context.isWikiAdmin && previous?.startsWith("general/jobs") && !path.startsWith("general/jobs")) void jobs.load();
  // Leaving the system log: the errors of the last hour are counted again.
  if (context.isWikiAdmin && previous?.startsWith("general/logs") && !path.startsWith("general/logs")) void logs.load();
});
function moduleItem(item: ModuleDescriptor): SettingsNavItem {
  const state = moduleStates.value[item.id];
  const alert = state && (state.tone === "warning" || state.tone === "danger");
  return {
    path: `general/modules/${item.id}`,
    label: item.title,
    icon: moduleEntry(item.settingsUi)?.icon ?? "blocks",
    count: alert ? 1 : undefined,
    tone: alert ? state.tone : undefined,
    countLabel: alert ? state.label : undefined,
    note: state && state.tone === "neutral" ? state.label.toLocaleLowerCase(locale) : undefined,
  };
}

// A failed ZIP run is already the issue of its job below, with a retry in place: the backup warning that says the same would be a second one (only BackupPolicy's failed-run line; a failed archive cleanup stays).
const backupWarning = computed(() => (jobs.failed.value.some((job) => job.id === "backups.zip") && context.backupWarning?.startsWith("Poslední zálohování selhalo") ? null : context.backupWarning));
const issues = computed(() => {
  const found = settingsIssues(registry.vaults.value, registry.locations.value, backupWarning.value, registry.loaded.value, {
    pendingRequests: requests?.count.value ?? 0,
    freshAuth: security?.me.value?.freshAuth ?? null,
    outsideWithoutNetworks: overviewFacts.outsideWithoutNetworks.value,
    levelsReview: overviewFacts.levelsReview,
  });
  // The rail marks AI when a model it uses is unavailable; Přehled says the same in words (refresh 2.5).
  if (aiIssues.value)
    found.push({ id: "ai", icon: "ai", tone: "warning", title: t.shell.aiIssue.title, detail: t.shell.aiIssue.detail, target: { path: "general/modules/ai/overview" }, action: t.shell.aiIssue.action });
  const dead = outbox.issue();
  if (dead) found.push(dead);
  found.push(...jobs.issues());
  const logged = logs.issue();
  if (logged) found.push(logged);
  return found;
});
const groups = computed<SettingsNavGroup[]>(() => {
  const troubled = registry.troubled.value.length;
  const pending = requests?.count.value ?? 0;
  const route = router.route.value.path;
  // The groups of Správa wiki, by what people look for: Přehled and Audit, Lidé, Prostory, Trezory a hesla,
  // AI a integrace, Provoz, Server a úložiště. Every section keeps its path; only the grouping and names changed.
  return [
    {
      id: "start",
      items: [
        { path: "security/overview", label: t.shell.nav.overview, icon: "layout-dashboard", count: issues.value.length || undefined, countLabel: t.shell.nav.toResolve },
        { path: "security/audit", label: t.shell.nav.audit, icon: "scroll-text" },
      ],
    },
    {
      id: "people",
      label: t.shell.nav.people,
      items: [
        { path: "people/accounts", label: t.shell.nav.people, icon: "account", count: directory.loaded.value ? directory.total.value : undefined, countLabel: t.shell.nav.peopleCount },
        { path: "people/groups", label: t.shell.nav.groups, icon: "users", count: directory.loaded.value ? directory.groups.value.length : undefined, countLabel: t.shell.nav.groupsCount },
        { path: "general/authentication", label: t.shell.nav.signIn, icon: "identity" },
      ],
    },
    {
      id: "spaces",
      label: t.shell.nav.spaces,
      items: [
        { path: "access/spaces", label: t.shell.nav.spacesAccess, icon: "book-open", count: context.spaces.length || undefined, countLabel: t.shell.nav.spacesCount },
        ...pluginKinds().map((kind) => ({ path: `access/${kind.id}`, label: kind.many, icon: kind.icon })),
        { path: "spaces/trash", label: t.shell.nav.spaceTrash, icon: "trash" },
        { path: "spaces/contacts", label: t.shell.nav.contacts, icon: "users" },
      ],
    },
    {
      id: "vaults",
      label: t.shell.nav.vaultsGroup,
      items: [
        {
          path: "security/vaults",
          label: t.shell.nav.vaults,
          icon: "lock",
          count: registry.loaded.value ? troubled || registry.vaults.value.length : undefined,
          tone: troubled ? "warning" : undefined,
          countLabel: troubled ? t.shell.nav.needsAttention : t.shell.nav.vaultsCount,
        },
        { path: "security/requests", label: t.shell.nav.requests, icon: "user-check", count: pending || undefined, tone: pending ? "warning" : undefined, countLabel: t.shell.nav.requestsWaiting },
        { path: "security/protection", label: t.shell.nav.protection, icon: "fingerprint" },
      ],
    },
    {
      id: "ai",
      label: t.shell.nav.aiGroup,
      items: [
        { path: "general/modules/ai", label: t.shell.nav.ai, icon: "ai", count: aiIssues.value || undefined, tone: aiIssues.value ? "warning" : undefined, countLabel: t.shell.nav.needsAttention },
        ...integrationModules.value.map(moduleItem),
        // The overview of every module stays one click away while the list is loading, when it failed, and on its own old link.
        ...(!modules.value || modulesFailed.value || route === "general/modules" ? [{ path: "general/modules", label: t.shell.nav.modules, icon: "blocks", exact: true }] : []),
        { path: "general/modules/wiki-js-import", label: t.shell.nav.wikiJs, icon: "upload" },
        { path: "people/tokens", label: t.shell.nav.tokens, icon: "key-round" },
      ],
    },
    {
      id: "operations",
      label: t.shell.nav.operations,
      items: [
        { path: "general/jobs", label: t.shell.nav.jobs, icon: "clock", count: jobs.failed.value.length || undefined, tone: jobs.failed.value.length ? "warning" : undefined, countLabel: t.shell.nav.jobsFailed },
        { path: "general/logs", label: t.shell.nav.logs, icon: "file-text", count: logs.errorsHour.value || undefined, tone: logs.errorsHour.value ? "danger" : undefined, countLabel: t.shell.nav.logsCount },
        { path: "general/backups", label: t.shell.nav.backups, icon: "download", count: context.backupWarning ? 1 : undefined, tone: context.backupWarning ? "warning" : undefined, countLabel: t.shell.nav.needsAttention },
      ],
    },
    {
      id: "server",
      label: t.shell.nav.server,
      items: [
        { path: "general/storage", label: t.shell.nav.storage, icon: "folder" },
        { path: "general/files", label: t.shell.nav.files, icon: "folder", count: registry.loaded.value ? registry.locations.value.length : undefined, countLabel: t.shell.nav.filesCount },
        { path: "security/networks", label: t.shell.nav.networks, icon: "network" },
      ],
    },
  ];
});

const segments = computed(() => router.route.value.segments);
const section = computed(() => segments.value.slice(0, 2).join("/"));
</script>

<template>
  <SettingsShell
    :title="t.shell.title"
    :eyebrow="t.shell.eyebrow"
    :subtitle="t.shell.subtitle"
    icon="settings"
    :groups="groups"
    :nav-label="t.shell.title"
  >
    <template v-if="flash.message.value" #notice>
      <ResultBanner :key="flash.message.value.key" :message="flash.message.value.text" :icon="flash.message.value.icon" @dismiss="flash.dismiss()" />
    </template>
    <template #rail-footer><AtlasVersionInfo class="global-settings-version" :version="context.version" href="#/whats-new" /></template>

    <!-- A section leaves with a fade while the next one rises in over it (settings.css, st-page): no hard cut to an empty area. -->
    <Transition name="st-page" @before-enter="swap.beforeEnter" @before-leave="swap.beforeLeave">
    <OverviewPage v-if="section === 'security/overview'" key="overview" :issues="issues" :facts="overviewFacts" :directory="directory" />
    <AccountsPage v-else-if="section === 'people/accounts'" key="accounts" :sub="segments[2]" :part="segments[3]" />
    <GroupsPage v-else-if="section === 'people/groups'" key="groups" :sub="segments[2]" :part="segments[3]" />
    <ApiTokensPage v-else-if="section === 'people/tokens'" key="tokens" @reviewed="overviewFacts.load()" />
    <SpacesAccessPage v-else-if="section === 'access/spaces'" :key="`spaces-${segments[2] ?? ''}`" :part="segments.slice(2)" />
    <AccessKindPage v-else-if="segments[0] === 'access' && segments[1]" :key="`kind-${segments[1]}`" :kind-id="segments[1]" :part="segments.slice(2)" />
    <AddVaultPage v-else-if="section === 'security/vaults' && segments[2] === 'new'" key="vault-new" back-path="security/vaults" />
    <AllVaultsPage v-else-if="section === 'security/vaults'" key="vaults" />
    <AccessHistoryPage v-else-if="section === 'security/audit'" key="audit" />
    <ProtectionPage v-else-if="section === 'security/protection'" :key="`protection-${segments[2] ?? ''}`" :changing="segments[2] === 'change'" />
    <NetworksPage v-else-if="section === 'security/networks'" :key="`networks-${segments[2] ?? ''}`" :adding="segments[2] === 'new'" />
    <AccessRequestsPage v-else-if="section === 'security/requests'" key="requests" />
    <AuthenticationPage v-else-if="section === 'general/authentication'" key="authentication" :part="segments[2]" />
    <ModulesPage v-else-if="section === 'general/modules'" key="modules" :sub="segments[2]" :part="segments[3]" />
    <BackupsPage v-else-if="section === 'general/backups'" key="backups" :part="segments[2]" />
    <JobsPage v-else-if="section === 'general/jobs'" :key="`jobs-${segments.slice(2).join('/')}`" :part="segments.slice(2)" />
    <LogsPage v-else-if="section === 'general/logs'" key="logs" />
    <SpaceTrashPage v-else-if="section === 'spaces/trash'" key="space-trash" />
    <ContactLinkPage v-else-if="section === 'spaces/contacts'" key="contacts" />
    <StorageProfilesPage v-else-if="section === 'general/storage'" key="storage" />
    <LocationsPage v-else-if="section === 'general/files'" :key="`files-${segments[2] ?? ''}`" :editing="segments[2]" />
    <EmptyState v-else icon="compass" :title="t.shell.unknownTitle" :text="t.shell.unknownText">
      <Button as-child variant="outline"><a :href="router.href('security/overview')"><AtlasIcon name="back" :size="16" />{{ t.shell.unknownAction }}</a></Button>
    </EmptyState>
    </Transition>
  </SettingsShell>
</template>

<style scoped>
.global-settings-version { margin-top: 12px; padding: 0 10px; }
</style>
