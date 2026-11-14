<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import CommandIcon from "../../CommandIcon.vue";
import { Button } from "../../ui/button";
import { errorMessage } from "../../../api";
import ActivityTimeline from "../ActivityTimeline.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import StatTile from "../StatTile.vue";
import StatusBadge from "../StatusBadge.vue";
import { auditActivity, loadAccessAudit } from "../auditActivity";
import type { ActivityItem } from "../activity";
import { useSettingsContext } from "../context";
import t from "../../../i18n/settings-global";
import { formatNumber } from "../../../i18n";
import { useSettingsRoute } from "../useSettingsRoute";
import { useVaultRegistry } from "../vaultRegistry";
import type { SettingsIssue } from "./issues";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import FreshAuthSelfTest from "../security/FreshAuthSelfTest.vue";
import { modeInfo } from "../security/protection";
import type { SecurityOverview } from "../security/securityOverview";
import { useSecurityState } from "../security/securityState";
import { useAccessRequestFeed } from "../requests/accessRequestFeed";
import type { DirectorySummary } from "../people/directorySummary";
import { useAiAdminHealth } from "../general/ai/useAiAdminHealth";

/**
 * Přehled of Nastavení wiki (refresh 2.5, board 06): what needs attention with one button each, the state of the
 * whole wiki in tiles (people, groups, spaces, requests, AI, backups, vaults with protection and file locations,
 * networks), frequent tasks as one grid of links, the latest audit events and the fresh sign-in self-test.
 */
const props = defineProps<{ issues: SettingsIssue[]; facts: SecurityOverview; directory?: DirectorySummary }>();
const context = useSettingsContext();
const router = useSettingsRoute();
const registry = useVaultRegistry();
const security = useSecurityState();
const requests = useAccessRequestFeed();
const ai = useAiAdminHealth();

const tasks = [
  { href: () => router.href("people/accounts/new"), icon: "account", title: t.overview.tasks.invite, key: "people/accounts/new" },
  { href: () => router.href("people/accounts"), icon: "user-check", title: t.overview.tasks.whoHasAccess, key: "people/accounts" },
  { href: () => "#/new-space", icon: "book-open", title: t.overview.tasks.newSpace, key: "new-space" },
  { href: () => router.href("general/modules/wiki-js-import"), icon: "upload", title: t.overview.tasks.importWikiJs, key: "general/modules/wiki-js-import" },
  { href: () => router.href("general/modules/ai"), icon: "ai", title: t.overview.tasks.setUpAi, key: "general/modules/ai" },
  { href: () => router.href("security/vaults/new"), icon: "lock", title: t.overview.tasks.addVault, key: "security/vaults/new" },
  { href: () => router.href("security/networks/new"), icon: "network", title: t.overview.tasks.addNetwork, key: "security/networks/new" },
  { href: () => router.href("security/protection"), icon: "fingerprint", title: t.overview.tasks.changeProtection, key: "security/protection" },
  { href: () => router.href("general/files/new"), icon: "folder-plus", title: t.overview.tasks.addLocation, key: "general/files/new" },
];

const spacesWithVaults = computed(() => new Set(registry.vaults.value.map((vault) => vault.spaceId)).size);
const troubled = computed(() => registry.troubled.value.length);
const pages = computed(() => context.spaces.reduce((sum, space) => sum + (space.pageCount ?? 0), 0));
const configuredModels = computed(() => ai.overview.value?.profiles.filter((profile) => profile.state !== "NotConfigured" && profile.kind !== "rerank").length ?? 0);
const aiValue = computed(() => {
  const overview = ai.overview.value;
  if (!overview) return ai.missing.value ? t.overview.aiSetUp : "…";
  if (!overview.enabled) return t.overview.aiOff;
  return ai.failures.value.length || ai.warnings.value.length ? t.overview.aiAttention : t.overview.aiOn;
});
const aiTone = computed(() => (!ai.overview.value ? undefined : !ai.overview.value.enabled || ai.failures.value.length || ai.warnings.value.length ? "warning" : "success"));

const recent = ref<ActivityItem[]>([]);
const recentError = ref("");
const recentLoading = ref(true);
let controller: AbortController | undefined;
onMounted(async () => {
  void props.facts.load();
  controller = new AbortController();
  try {
    recent.value = (await loadAccessAudit({ limit: 5 }, controller.signal)).items.map(auditActivity);
  } catch (failure) {
    if (!controller.signal.aborted) recentError.value = errorMessage(failure);
  } finally {
    recentLoading.value = false;
  }
});
onBeforeUnmount(() => controller?.abort());

const issueHref = (issue: SettingsIssue) => ("path" in issue.target ? router.href(issue.target.path) : issue.target.hash);
/** An issue fixed in place (the outbox retry): its button waits for the answer, a failure says so in the row's place, a success is
 *  confirmed and takes the keyboard (UX3-5), since the row and its button usually go with it. */
const running = ref("");
const runError = ref("");
const runDone = ref("");
const doneNote = ref<HTMLElement>();
async function runIssue(issue: SettingsIssue) {
  if (running.value || !issue.run) return;
  running.value = issue.id;
  runError.value = "";
  runDone.value = "";
  try {
    await issue.run();
    runDone.value = issue.done ?? t.overview.done;
    await nextTick();
    doneNote.value?.focus();
  } catch (failure) {
    runError.value = errorMessage(failure);
  } finally {
    running.value = "";
  }
}
</script>

<template>
  <SettingsPage :title="t.overview.title" :description="t.overview.description">
    <SettingsSection v-if="issues.length" class="overview-issues" :title="t.overview.attention" icon="triangle-alert" tone="warning">
      <template #actions><span class="overview-issues-count">{{ t.counts.things(issues.length) }}</span></template>
      <SettingsList :label="t.issues.listLabel">
        <SettingsListRow v-for="(issue, index) in issues" :key="issue.id" :title="issue.title" :description="issue.detail" :icon="issue.icon" :icon-tone="issue.tone === 'info' ? 'warning' : issue.tone" :data-issue="issue.id">
          <template #actions>
            <Button v-if="issue.run" :variant="index === 0 ? 'default' : 'outline'" size="sm" type="button" :disabled="running === issue.id" :aria-busy="running === issue.id" :data-issue-run="issue.id" @click="runIssue(issue)">{{ issue.action }}</Button>
            <Button v-else :variant="index === 0 ? 'default' : 'outline'" size="sm" as-child><a :href="issueHref(issue)">{{ issue.action }}</a></Button>
          </template>
        </SettingsListRow>
      </SettingsList>
      <p v-if="runError" class="st-note" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ runError }}</p>
    </SettingsSection>
    <SettingsSection v-else class="overview-issues" :title="t.overview.nothingTitle" icon="circle-check" tone="success" :description="t.overview.nothingText" data-issues-none />
    <p v-if="runDone" ref="doneNote" class="st-note overview-issue-done" role="status" tabindex="-1" data-issue-done><AtlasIcon name="circle-check" :size="15" />{{ runDone }}</p>

    <section class="st-group" aria-labelledby="overview-state">
      <h3 id="overview-state" class="st-group-label">{{ t.overview.state }}</h3>
      <div class="overview-tiles">
        <StatTile v-if="context.isWikiAdmin" :label="t.overview.people" icon="account" :value="directory?.loaded.value ? String(directory.total.value) : '…'" :href="router.href('people/accounts')" data-stat="people">
          <template v-if="directory?.loaded.value">{{ directory.pending.value ? t.overview.pendingActivation(formatNumber(directory.pending.value)) : t.overview.allActivated }}</template>
        </StatTile>
        <StatTile v-if="context.isWikiAdmin" :label="t.overview.groups" icon="users" :value="directory?.loaded.value ? String(directory.groups.value.length) : '…'" :href="router.href('people/groups')" data-stat="groups">
          <template v-if="directory?.loaded.value">{{ directory.hubGroups.value ? t.overview.fromHub(t.counts.groups(directory.hubGroups.value)) : t.overview.noHubSync }}</template>
        </StatTile>
        <StatTile :label="t.overview.spaces" icon="book-open" :value="String(context.spaces.length)" :href="router.href('access/spaces')" data-stat="spaces">
          {{ t.overview.pagesTotal(t.counts.pages(pages)) }}
        </StatTile>
        <StatTile :label="t.overview.requests" icon="user-check" :tone="requests?.count.value ? 'warning' : 'success'" :value="requests?.loaded.value ? (requests.count.value ? t.counts.waiting(requests.count.value) : t.overview.requestsNone) : '…'" :href="router.href('security/requests')" data-stat="requests">
          {{ requests?.count.value ? t.overview.requestsHandle : t.overview.requestsAllDone }}
        </StatTile>
        <StatTile :label="t.overview.ai" icon="ai" :tone="aiTone" :value="aiValue" :href="router.href('general/modules/ai')" data-stat="ai">
          <template v-if="ai.warnings.value.length">{{ ai.warnings.value[0].warning }}</template>
          <template v-else-if="ai.overview.value">{{ t.overview.aiModels(configuredModels) }}</template>
        </StatTile>
        <StatTile :label="t.overview.backups" icon="download" :tone="context.backupWarning ? 'warning' : 'success'" :value="context.backupWarning ? t.overview.backupsAttention : t.overview.backupsOk" :href="router.href('general/backups')" data-stat="backups">
          <template v-if="context.backupWarning">{{ context.backupWarning }}</template>
        </StatTile>
        <StatTile :label="t.overview.vaults" icon="lock" :value="registry.loaded.value ? t.counts.vaults(registry.vaults.value.length) : '…'" :href="router.href('security/vaults')" data-stat="vaults">
          <StatusBadge v-if="troubled" tone="warning" size="sm" :label="t.counts.needAttention(troubled)" />
          <span class="overview-facts">
            <span v-if="registry.loaded.value">{{ t.counts.spaces(spacesWithVaults) }}</span>
            <span data-stat="protection">{{ t.overview.protection(facts.mode ? modeInfo[facts.mode].label : facts.error ? t.overview.protectionNotLoaded : "…") }}</span>
            <span v-if="registry.loaded.value" data-stat="locations">{{ registry.locations.value.length ? t.counts.fileLocations(registry.locations.value.length) : t.overview.noLocations }}</span>
          </span>
        </StatTile>
        <StatTile :label="t.overview.networks" icon="network" :tone="facts.outsideWithoutNetworks ? 'warning' : undefined" :value="facts.loaded && !facts.error ? t.counts.networks(facts.activeNetworks) : '…'" :href="router.href('security/networks')" data-stat="networks">
          <template v-if="facts.outsideWithoutNetworks">{{ t.overview.outsideWithoutNetworks }}</template>
          <template v-else-if="facts.loaded && facts.networks > facts.activeNetworks">{{ t.counts.turnedOff(facts.networks - facts.activeNetworks) }}</template>
          <template v-else-if="facts.loaded && !facts.networks">{{ t.overview.noTrustedNetworks }}</template>
        </StatTile>
      </div>
    </section>

    <section class="st-group" aria-labelledby="overview-tasks">
      <h3 id="overview-tasks" class="st-group-label">{{ t.overview.tasksTitle }}</h3>
      <ul class="overview-tasks">
        <li v-for="task in tasks" :key="task.key">
          <a class="overview-task" :href="task.href()" :data-task="task.key">
            <CommandIcon :name="task.icon" :size="16" class="overview-task-icon" />
            <span class="overview-task-label">{{ task.title }}</span>
            <AtlasIcon name="right" :size="14" class="overview-task-chevron" />
          </a>
        </li>
      </ul>
    </section>

    <SettingsSection :title="t.overview.recent" icon="activity">
      <template #actions><a class="st-link" :href="router.href('security/audit')">{{ t.overview.fullAudit }}<AtlasIcon name="arrow-right" :size="14" /></a></template>
      <LoadingSkeleton v-if="recentLoading && !recent.length" :rows="4" row-height="44px" :label="t.overview.loadingAudit" />
      <p v-else-if="recentError" class="st-note" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ recentError }}</p>
      <ActivityTimeline v-else :items="recent" compact :label="t.overview.recent" :empty-title="t.overview.recentEmptyTitle" :empty-text="t.overview.recentEmptyText" />
    </SettingsSection>
    <SettingsSection :title="t.overview.freshAuth" icon="shield-check" advanced :summary="t.overview.freshAuthSummary">
      <FreshAuthSelfTest :fresh-auth="security?.me.value?.freshAuth ?? null" />
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.overview-facts > span:not(:last-child)::after { content: " · "; }
.overview-issues-count { color: var(--warning); font-size: 13px; font-weight: 500; }
.overview-issue-done { color: var(--text); animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.overview-issue-done > svg { color: var(--success); }
.overview-issue-done:focus { outline: none; }
.overview-tiles { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; }
@container settings-shell (max-width: 900px) { .overview-tiles { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
.overview-tasks {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  margin: 0;
  padding: 0;
  list-style: none;
  overflow: hidden;
  border: 1px solid var(--line);
  border-radius: var(--st-radius);
  background: var(--surface);
}
/* One soft line between neighbours: each cell draws its right and bottom edge, the outer frame hides the last ones. */
.overview-tasks > li { min-width: 0; margin: 0 -1px -1px 0; border-right: 1px solid var(--st-line-soft); border-bottom: 1px solid var(--st-line-soft); }
.overview-task {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 52px;
  padding: 0 16px;
  color: var(--text);
  font-weight: 500;
  text-decoration: none;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out);
}
.overview-task-label { flex: 1; min-width: 0; }
@media (hover: hover) and (pointer: fine) {
  .overview-task:hover { background: var(--st-subtle); color: var(--accent); }
}
.overview-task:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; --focus-offset: -2px; }
.overview-task-icon { flex-shrink: 0; color: var(--accent); }
.overview-task-chevron { flex-shrink: 0; color: var(--muted); transition: translate var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) {
  .overview-task:hover .overview-task-chevron { translate: calc(var(--atlas-lift) * 1.5) 0; color: var(--accent); }
}
@container settings-shell (max-width: 900px) { .overview-tasks { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@container settings-shell (max-width: 560px) { .overview-tasks { grid-template-columns: minmax(0, 1fr); } }
</style>
