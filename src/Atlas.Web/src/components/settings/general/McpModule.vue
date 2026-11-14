<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from "vue";
import { Switch } from "../../ui/switch";
import { api, put } from "../../../api";
import t from "../../../i18n/settings-modules";
import { popStatus, revealHeight } from "../../../motion/aiMotion";
import ConflictNote from "../ConflictNote.vue";
import ErrorState from "../ErrorState.vue";
import ImpactPreview from "../ImpactPreview.vue";
import LoadingNote from "../LoadingNote.vue";
import SaveBar from "../SaveBar.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import CopyField from "./CopyField.vue";
import { mcpState, type McpSettings } from "./moduleState";
import { useDraftGuard } from "../dirtyGuard";
import { useSettingsDocument } from "../useSettingsDocument";
import { useModuleHeader } from "./moduleHeader";
import ModuleContributions from "./ModuleContributions.vue";

/**
 * Server MCP (module `mcp`): one wiki-wide switch for the Model Context Protocol endpoint that lets
 * tools such as Claude Code work with the wiki through a personal token with the MCP channel. It is
 * not an AI feature of Atlas (no model runs here). Off by default, also after an upgrade (O2).
 * Saved explicitly with the SaveBar (one PUT with the CAS version); the server address is shown
 * only while the module is on, and a pending change shows its consequences first.
 */
const endpoint = "/admin/mcp";
const enabled = ref(false);
const guideHost = ref<HTMLElement>();
const badgeHost = ref<HTMLElement>();
const { saved: state, loading, loadError, saving, saveError, conflict, dirty, load, save, reset, reloadAfterConflict } = useSettingsDocument<McpSettings>({
  load: () => api<McpSettings>(endpoint),
  save: (current) => put<McpSettings>(endpoint, { enabled: enabled.value, expectedVersion: current.version }),
  fill: (current) => { enabled.value = current.enabled; },
  dirty: (current) => enabled.value !== current.enabled,
});

const status = computed(() => (state.value ? mcpState(state.value) : undefined));
// Refresh 2.5 module template (board 10): the switch in the page header, what the module adds below the settings.
const header = useModuleHeader();
const contributions = t.mcp.contributions;
const url = computed(() => (state.value?.enabled && state.value.url ? state.value.url : ""));
const impact = computed(() =>
  enabled.value
    ? [
        { icon: "plug", label: t.mcp.impact.onToken, tone: "success" as const },
        { icon: "shield-check", label: t.mcp.impact.onWriteLabel, detail: t.mcp.impact.onWriteDetail },
        { icon: "globe", label: t.mcp.impact.onModelLabel, detail: t.mcp.impact.onModelDetail },
      ]
    : [
        { icon: "ban", label: t.mcp.impact.offLabel, detail: t.mcp.impact.offDetail, tone: "warning" as const },
        { icon: "code-xml", label: t.mcp.impact.restLabel },
      ],
);

// Motion only for a change made here: the state pops and the guide unfolds when the server turns on.
watch(
  () => state.value?.enabled,
  (now, before) => {
    if (before === undefined || now === before) return;
    void nextTick(() => {
      if (badgeHost.value) popStatus(badgeHost.value);
      if (now && guideHost.value) revealHeight(guideHost.value);
    });
  },
);

useDraftGuard(dirty, { save, discard: reset });
onMounted(load);
</script>

<template>
  <div class="mcp-module" role="region" :aria-label="t.mcp.regionLabel">
    <ConflictNote v-if="conflict" :text="t.common.conflictChange" :reload-label="t.common.reload" :busy="loading || saving" data-module-conflict @reload="reloadAfterConflict" @dismiss="conflict = false" />
    <ErrorState v-if="loadError" :title="t.mcp.loadFailed" :text="loadError" :busy="loading" @retry="load" />
    <LoadingNote v-else-if="loading && !state" :label="t.mcp.loading" />
    <template v-else-if="state">
      <SettingsSection :title="t.mcp.title" icon="plug" :description="t.mcp.description">
        <template #actions><span ref="badgeHost" class="mcp-badge"><StatusBadge v-if="status" :tone="status.tone" :icon="status.icon" :label="status.label" size="sm" data-mcp-status /></span></template>
        <Teleport v-if="header?.target.value" :to="header.target.value">
          <label class="st-switch-pill" data-module-switch><Switch v-model="enabled" :disabled="saving" :aria-label="t.mcp.enableLabel" data-mcp-enabled />{{ enabled ? t.common.on : t.common.off }}</label>
        </Teleport>
        <SettingsRow v-if="!header" v-slot="{ id, describedBy }" :label="t.mcp.enableLabel" :help="t.mcp.enableHelp">
          <Switch :id="id" v-model="enabled" :aria-describedby="describedBy" :disabled="saving" data-mcp-enabled />
        </SettingsRow>
        <p v-else class="st-note">{{ t.mcp.headerNote }}</p>
        <ImpactPreview v-if="dirty" :title="enabled ? t.mcp.impact.titleOn : t.mcp.impact.titleOff" :items="impact" :unchanged="enabled ? t.mcp.impact.unchangedOn : t.mcp.impact.unchangedOff" data-mcp-impact />
      </SettingsSection>

      <div ref="guideHost" class="mcp-guide-host">
        <SettingsSection v-if="url" :title="t.mcp.guide.title" icon="link" :description="t.mcp.guide.description" data-mcp-guide>
          <CopyField :label="t.mcp.guide.address" :value="url" :copy-label="t.common.copyAddress" :value-attrs="{ 'data-mcp-url': '' }" />
          <p class="mcp-help">{{ t.mcp.guide.help }}</p>
        </SettingsSection>
      </div>
      <ModuleContributions v-if="header" :items="contributions" />
      <SaveBar :dirty="dirty" :saving="saving" :error="saveError" :save-label="t.common.save" @save="save" @cancel="reset" />
    </template>
  </div>
</template>

<style scoped>
.mcp-module { display: grid; gap: 18px; min-width: 0; }
.mcp-badge { display: inline-flex; }
.mcp-help { margin: 0; font-size: 13px; opacity: 0.8; }
</style>
