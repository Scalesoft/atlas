<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { Switch } from "../../ui/switch";
import { api, put } from "../../../api";
import t from "../../../i18n/settings-modules";
import ConflictNote from "../ConflictNote.vue";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";
import SaveBar from "../SaveBar.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import { editorAssistState, type EditorAssistSettings } from "./moduleState";
import { useDraftGuard } from "../dirtyGuard";
import { useSettingsDocument } from "../useSettingsDocument";
import { useModuleHeader } from "./moduleHeader";
import ModuleContributions from "./ModuleContributions.vue";

/**
 * Návrhy při psaní (module `editor-assist`): one wiki-wide switch for the rule-based editor
 * suggestions. On until a wiki administrator turns it off; no model is called. Saved explicitly
 * with the SaveBar (one PUT with the CAS version). Users read the result as `features.editorAssist`.
 */
const endpoint = "/admin/modules/editor-assist";
const enabled = ref(true);
const { saved: state, loading, loadError, saving, saveError, conflict, dirty, load, save, reset, reloadAfterConflict } = useSettingsDocument<EditorAssistSettings>({
  load: () => api<EditorAssistSettings>(endpoint),
  save: (current) => put<EditorAssistSettings>(endpoint, { enabled: enabled.value, expectedVersion: current.version }),
  fill: (current) => { enabled.value = current.enabled; },
  dirty: (current) => enabled.value !== current.enabled,
});

const status = computed(() => (state.value ? editorAssistState(state.value) : undefined));
// Refresh 2.5 module template (board 10): the switch in the page header, what the module adds below the settings.
const header = useModuleHeader();
const contributions = t.editorAssist.contributions;

useDraftGuard(dirty, { save, discard: reset });
onMounted(load);
</script>

<template>
  <div class="editor-assist-module" role="region" :aria-label="t.editorAssist.regionLabel">
    <ConflictNote v-if="conflict" :text="t.common.conflictChange" :reload-label="t.common.reload" :busy="loading || saving" data-module-conflict @reload="reloadAfterConflict" @dismiss="conflict = false" />
    <ErrorState v-if="loadError" :title="t.editorAssist.loadFailed" :text="loadError" :busy="loading" @retry="load" />
    <LoadingNote v-else-if="loading && !state" :label="t.editorAssist.loading" />
    <template v-else-if="state">
      <SettingsSection :title="t.editorAssist.title" icon="lightbulb" :description="t.editorAssist.description">
        <template #actions><StatusBadge v-if="status" :tone="status.tone" :icon="status.icon" :label="status.label" size="sm" data-editor-assist-status /></template>
        <Teleport v-if="header?.target.value" :to="header.target.value">
          <label class="st-switch-pill" data-module-switch><Switch v-model="enabled" :disabled="saving" :aria-label="t.editorAssist.enableLabel" data-editor-assist-enabled />{{ enabled ? t.common.on : t.common.off }}</label>
        </Teleport>
        <SettingsRow v-if="!header" v-slot="{ id, describedBy }" :label="t.editorAssist.enableLabel" :help="t.editorAssist.enableHelp">
          <Switch :id="id" v-model="enabled" :aria-describedby="describedBy" :disabled="saving" data-editor-assist-enabled />
        </SettingsRow>
        <p v-else class="st-note">{{ t.editorAssist.headerNote }}</p>
      </SettingsSection>
      <ModuleContributions v-if="header" :items="contributions" />
      <SaveBar :dirty="dirty" :saving="saving" :error="saveError" :save-label="t.common.save" @save="save" @cancel="reset" />
    </template>
  </div>
</template>

<style scoped>
.editor-assist-module { display: grid; gap: 18px; min-width: 0; }
</style>
