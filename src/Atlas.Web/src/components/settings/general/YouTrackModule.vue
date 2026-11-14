<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { scrollBehavior } from "../../../motion/aiMotion";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { Checkbox } from "../../ui/checkbox";
import { Switch } from "../../ui/switch";
import { api, errorMessage, post, put } from "../../../api";
import t from "../../../i18n/settings-modules";
import ConflictNote from "../ConflictNote.vue";
import ErrorState from "../ErrorState.vue";
import InlineReveal from "../InlineReveal.vue";
import LoadingNote from "../LoadingNote.vue";
import SaveBar from "../SaveBar.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import FlowProgress from "../FlowProgress.vue";
import { youTrackState, type YouTrackSettings } from "./moduleState";
import { useDraftGuard } from "../dirtyGuard";
import { useSettingsDocument } from "../useSettingsDocument";
import { disclosureHooks } from "../disclosure";
import { useModuleHeader } from "./moduleHeader";
import ModuleContributions from "./ModuleContributions.vue";

/**
 * YouTrack (module `youtrack`): the connection (switch, address, service token) and the projects
 * each space may search. Saved explicitly with the SaveBar (one PUT with the CAS version); the
 * connection test runs against the stored settings only, so it is closed while there are changes.
 */
interface ConnectionTest {
  ok: boolean;
  checkedAt: string;
  message: string;
  projects: { id: string; name: string; shortName: string }[];
}
const endpoint = "/admin/integrations/youtrack";
const enabled = ref(false);
const baseUrl = ref("");
const token = ref("");
const clearToken = ref(false);
const projects = ref<Record<string, string>>({});
const testing = ref(false);
const testError = ref("");
const connection = ref<ConnectionTest>();

const { saved: state, loading, loadError, saving, saveError, conflict, dirty, load, save, reset, reloadAfterConflict, isDisposed } = useSettingsDocument<YouTrackSettings>({
  load: () => api<YouTrackSettings>(endpoint),
  save: async (current) => {
    // Credentials stay only in the request and the field; never in storage, URLs or messages.
    // The finally also covers building the body, so a typed token never outlives an attempt.
    try {
      const body = {
        enabled: enabled.value,
        baseUrl: baseUrl.value.trim(),
        token: token.value || undefined,
        clearToken: clearToken.value,
        expectedVersion: current.version,
        spaceProjects: current.spaces.map((space) => ({ spaceId: space.id, projectIds: [...new Set((projects.value[space.id] ?? "").split(/[\s,;]+/).filter(Boolean))] })),
      };
      return await put<YouTrackSettings>(endpoint, body);
    } finally {
      token.value = "";
    }
  },
  fill: (current) => {
    enabled.value = current.enabled;
    baseUrl.value = current.baseUrl;
    token.value = "";
    clearToken.value = false;
    projects.value = Object.fromEntries(current.spaces.map((space) => [space.id, storedProjects(space.id)]));
  },
  dirty: (current) => enabled.value !== current.enabled || baseUrl.value !== current.baseUrl || token.value.length > 0 || clearToken.value || current.spaces.some((space) => (projects.value[space.id] ?? "") !== storedProjects(space.id)),
});
const storedProjects = (spaceId: string): string => state.value?.spaceProjects.find((item) => item.spaceId === spaceId)?.projectIds.join(", ") ?? "";
const status = computed(() => (state.value ? youTrackState(state.value) : undefined));
// Refresh 2.5 module template (board 10): the switch sits in the page header, the saved state as a strip of steps.
const header = useModuleHeader();
const linked = computed(() => !!state.value?.spaceProjects.some((item) => item.projectIds.length));
const steps = computed(() => {
  const current = state.value;
  const connected = !!current?.baseUrl && !!current.hasToken;
  const done = [connected, linked.value, !!current?.enabled];
  const next = done.findIndex((value) => !value);
  const step = (index: number) => (done[index] ? "done" : index === next ? "current" : "todo") as "done" | "current" | "todo";
  return [
    { id: "connection", label: t.youTrack.steps.connection.label, detail: connected ? t.youTrack.steps.connection.done : t.youTrack.steps.connection.todo, state: step(0), available: true, controls: "youtrack-connection" },
    { id: "projects", label: t.youTrack.steps.projects.label, detail: linked.value ? t.youTrack.steps.projects.done : connected ? t.youTrack.steps.projects.assign : t.youTrack.steps.projects.afterConnect, state: step(1), available: true, controls: "youtrack-projects" },
    { id: "enable", label: t.youTrack.steps.enable.label, detail: current?.enabled ? t.youTrack.steps.enable.on : t.youTrack.steps.enable.off, state: step(2), available: true },
  ];
});
function focusStep(id: string) {
  const target = id === "enable" ? header?.target.value : document.getElementById(id === "projects" ? "youtrack-projects" : "youtrack-connection");
  target?.scrollIntoView({ block: "center", behavior: scrollBehavior(target) });
  (target?.querySelector("input, button") as HTMLElement | null)?.focus({ preventScroll: true });
}
const contributions = t.youTrack.contributions;
// A changed draft makes an earlier test result stale.
watch([enabled, baseUrl, token, clearToken, projects], () => {
  connection.value = undefined;
  testError.value = "";
}, { deep: true });

async function testConnection() {
  const current = state.value;
  if (!current || dirty.value || testing.value || !current.moduleEnabled) return;
  testing.value = true;
  testError.value = "";
  connection.value = undefined;
  try {
    const result = await post<ConnectionTest>(`${endpoint}/test`);
    if (!isDisposed()) connection.value = result;
  } catch (failure) {
    if (!isDisposed()) testError.value = errorMessage(failure);
  } finally {
    testing.value = false;
  }
}
function setClearToken(value: boolean) {
  clearToken.value = value;
  if (value) token.value = "";
}

useDraftGuard(dirty, { save, discard: reset });
onMounted(load);
onBeforeUnmount(() => (token.value = ""));
</script>

<template>
  <div class="youtrack-module" role="region" :aria-label="t.youTrack.regionLabel">
    <ConflictNote v-if="conflict" :text="t.common.conflictChanges" :reload-label="t.common.reload" :busy="loading || saving" data-module-conflict @reload="reloadAfterConflict" @dismiss="conflict = false" />
    <ErrorState v-if="loadError" :title="t.youTrack.loadFailed" :text="loadError" :busy="loading" @retry="load" />
    <LoadingNote v-else-if="loading && !state" :label="t.youTrack.loading" />
    <template v-else-if="state">
      <Teleport v-if="header?.target.value" :to="header.target.value">
        <label class="st-switch-pill" data-module-switch><Switch v-model="enabled" :disabled="saving" :aria-label="t.youTrack.useConnection" data-youtrack-enabled />{{ enabled ? t.common.on : t.common.off }}</label>
      </Teleport>
      <FlowProgress v-if="header" variant="strip" :items="steps" :label="t.youTrack.flowLabel" @select="focusStep" />

      <SettingsSection id="youtrack-connection" :title="t.youTrack.connection.title" icon="plug" :description="t.youTrack.connection.description">
        <template v-if="!header" #actions><StatusBadge v-if="status" :tone="status.tone" :icon="status.icon" :label="status.label" size="sm" data-youtrack-status /></template>
        <SettingsRow v-if="!header" v-slot="{ id, describedBy }" :label="t.youTrack.useConnection" :help="state.moduleEnabled ? undefined : t.youTrack.moduleOffHelp">
          <Switch :id="id" v-model="enabled" :aria-describedby="describedBy" :disabled="saving" data-youtrack-enabled />
        </SettingsRow>
        <p v-else-if="!state.moduleEnabled" class="st-note"><AtlasIcon name="info" :size="15" />{{ t.youTrack.moduleOffHelp }}</p>
        <SettingsRow v-slot="{ id, describedBy }" :label="t.youTrack.url.label" :help="t.youTrack.url.help">
          <input :id="id" v-model="baseUrl" class="st-input" inputmode="url" maxlength="2048" autocomplete="off" spellcheck="false" :placeholder="t.youTrack.url.placeholder" :aria-describedby="describedBy" :disabled="saving" data-youtrack-url />
        </SettingsRow>
        <SettingsRow v-slot="{ id, describedBy }" :label="t.youTrack.token.label" :help="t.youTrack.token.help">
          <input
            :id="id"
            v-model="token"
            class="st-input"
            type="password"
            autocomplete="new-password"
            maxlength="8192"
            :disabled="clearToken || saving"
            :placeholder="state.hasToken ? t.youTrack.token.stored : t.youTrack.token.placeholder"
            :aria-describedby="describedBy"
            data-youtrack-token
          />
          <label v-if="state.hasToken" class="youtrack-check"><Checkbox :model-value="clearToken" :disabled="saving" data-youtrack-clear-token @update:model-value="setClearToken($event === true)" />{{ t.youTrack.token.clear }}</label>
        </SettingsRow>
        <!-- The outcome of the test lives in the footer band, so the band stays the card's bottom edge whether or not there is one. -->
        <div class="st-card-footer youtrack-footer">
          <div class="youtrack-footer-line">
            <p class="st-card-footer-text">{{ dirty ? t.youTrack.footerDirty : t.youTrack.footerTest }}</p>
            <Button variant="outline" size="sm" type="button" :disabled="dirty || testing || !state.enabled || !state.moduleEnabled || !state.hasToken" data-youtrack-test @click="testConnection">
              <AtlasIcon :name="testing ? 'loader-circle' : 'plug'" :size="15" :class="{ 'atlas-spinner': testing }" />{{ t.youTrack.test }}
            </Button>
          </div>
          <Transition :css="false" v-bind="disclosureHooks">
            <p v-if="testError" class="youtrack-error" role="alert" data-youtrack-test-error><AtlasIcon name="triangle-alert" :size="14" />{{ testError }}</p>
          </Transition>
          <InlineReveal :open="!!connection" class="youtrack-outcome">
            <div v-if="connection" class="youtrack-result" data-youtrack-test-result>
              <StatusBadge :tone="connection.ok ? 'success' : 'danger'" :label="connection.message" />
              <SettingsList v-if="connection.projects.length" :label="t.youTrack.projectsLabel">
                <SettingsListRow v-for="project in connection.projects" :key="project.id" :title="project.name" :description="project.shortName" icon="list">
                  <template #meta><code class="st-mono">{{ project.id }}</code></template>
                </SettingsListRow>
              </SettingsList>
              <p v-else class="st-note"><AtlasIcon name="info" :size="15" />{{ t.youTrack.noProjects }}</p>
            </div>
          </InlineReveal>
        </div>
      </SettingsSection>

      <SettingsSection id="youtrack-projects" :title="t.youTrack.projects.title" icon="book-open" :description="t.youTrack.projects.description">
        <SettingsRow v-for="space in state.spaces" :key="space.id" v-slot="{ id }" :label="space.name">
          <input :id="id" v-model="projects[space.id]" class="st-input st-mono" :aria-label="t.youTrack.projects.allowed(space.name)" :placeholder="t.youTrack.projects.placeholder" maxlength="10100" autocomplete="off" spellcheck="false" :disabled="saving" :data-youtrack-space="space.id" />
        </SettingsRow>
        <p v-if="!state.spaces.length" class="st-note"><AtlasIcon name="info" :size="15" />{{ t.youTrack.projects.noSpaces }}</p>
        <p class="st-note"><AtlasIcon name="users" :size="15" />{{ t.youTrack.projects.snapshot }}</p>
      </SettingsSection>

      <ModuleContributions v-if="header" :items="contributions" />
      <SaveBar :dirty="dirty" :saving="saving" :error="saveError" :save-label="t.youTrack.save" @save="save" @cancel="reset" />
    </template>
  </div>
</template>

<style scoped>
.youtrack-module { display: grid; gap: 18px; min-width: 0; }
.youtrack-check { display: flex; align-items: center; gap: 8px; font-size: 13px; cursor: pointer; }
.youtrack-error { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 12.5px; font-weight: 550; }
/* The footer band is a column: the note and the button on one line, then the outcome of the test under them. */
.youtrack-module .youtrack-footer { flex-direction: column; flex-wrap: nowrap; align-items: stretch; gap: 10px; }
.youtrack-footer-line { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; }
.youtrack-footer-line > .st-card-footer-text { flex: 1 1 240px; margin: 0; color: var(--muted); font-size: 13px; }
/* The outcome is a flex item of the footer (10 px gap): closed it takes that gap back, open it carries it as padding. */
.youtrack-outcome { min-width: 0; margin-top: -10px; }
.youtrack-result { display: grid; gap: 8px; justify-items: start; padding-top: 14px; }
.youtrack-result > :deep(.settings-list) { justify-self: stretch; max-height: 280px; overflow: auto; }
</style>
