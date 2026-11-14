<script setup lang="ts">
import { computed, ref } from "vue";
import { ApiError, errorMessage } from "../../../api";
import { useSettingsContext } from "../context";
import { daysProblem, maxCleanupDays, minCleanupDays, spaceTrashCleanupApi, type SpaceTrashCleanup, type SpaceTrashCleanupApi } from "../../../trashCleanupApi";
import { Button } from "../../ui/button";
import { Input } from "../../ui/input";
import ConfirmDialog from "../ConfirmDialog.vue";
import ConflictNote from "../ConflictNote.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";
import OptionPills from "../OptionPills.vue";
import SaveBar from "../SaveBar.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatTile from "../StatTile.vue";
import { useDraftGuard } from "../dirtyGuard";
import { useSettingsFlash } from "../flash";
import t from "../../../i18n/settings-space";
import { useSettingsDocument } from "../useSettingsDocument";

/**
 * Koš (unified trash spec §4b): what lies in the trash of this space and which unused objects wait, the rule that cleans
 * them up (manual by default) and a button that runs the rule now. When the wiki decides for all spaces, the rule is shown
 * read-only. The server answers 403 or 404 to everyone who is not an administrator of the space, and then the section does not exist.
 */
const props = withDefaults(defineProps<{ spaceId: string; api?: SpaceTrashCleanupApi }>(), { api: () => spaceTrashCleanupApi });
const flash = useSettingsFlash();
const context = useSettingsContext();

const mode = ref("manual");
const days = ref("60");
const { saved, loading, loadError, loadFailure, saving, saveError, conflict, dirty, load, save: saveDocument, reset: resetDocument, reloadAfterConflict } = useSettingsDocument<SpaceTrashCleanup>({
  load: (signal) => props.api.load(props.spaceId, signal),
  save: (current) => props.api.save(props.spaceId, { automatic: mode.value === "auto", days: Number(days.value) }, current.version),
  fill: (next) => { mode.value = next.automatic ? "auto" : "manual"; days.value = String(next.days); },
  dirty: (next) => mode.value !== (next.automatic ? "auto" : "manual") || Number(days.value) !== next.days,
  beforeSave: () => (mode.value === "auto" ? daysProblem(Number(days.value)) : undefined),
  onSaved: () => flash.notify(t.trash.saved, { icon: "trash" }),
  clearConflictOnSave: true,
  quietConflict: true,
});
// A 403 or 404 means that the section does not exist, so the page says that instead of the load error.
const unavailable = computed(() => loadFailure.value instanceof ApiError && [403, 404].includes(loadFailure.value.status));
const dayProblem = computed(() => (dirty.value && mode.value === "auto" ? daysProblem(Number(days.value)) : undefined));

const modes = [
  { value: "manual", label: t.trash.manual, icon: "user" },
  { value: "auto", label: t.trash.automatic, icon: "clock" },
];
const itemsText = (n: number) => t.trash.items(n);
const objectsText = (n: number) => t.trash.objects(n);
const ruleText = computed(() => (saved.value?.effectiveAutomatic ? t.trash.ruleAuto(saved.value.effectiveDays) : t.trash.ruleManual));

const confirming = ref(false), running = ref(false), runError = ref("");
// What the dialog promised, kept while it closes: the counts reload under it after the run.
const promised = ref({ due: 0, unused: 0, days: 0 });
function ask() {
  if (!saved.value) return;
  promised.value = { due: saved.value.counts.trashedDue, unused: saved.value.counts.unusedDue, days: saved.value.effectiveDays };
  runError.value = "";
  confirming.value = true;
}
async function run() {
  running.value = true;
  runError.value = "";
  try {
    const result = await props.api.run(props.spaceId);
    confirming.value = false;
    const skipped = result.skipped ? t.trash.skipped(result.skipped) : "";
    flash.notify(t.trash.cleaned(result.purged, result.trashed, skipped), { icon: "trash" });
    await load();
  } catch (failure) {
    runError.value = errorMessage(failure);
  } finally {
    running.value = false;
  }
}

function reset() {
  if (!saving.value) resetDocument();
}
function save() {
  if (dirty.value) return saveDocument();
}
useDraftGuard(dirty, { save, discard: reset });
void load();
</script>

<template>
  <EmptyState v-if="unavailable" icon="compass" :title="t.missing.title" :text="t.missing.text" />
  <SettingsPage v-else :title="t.trash.title" icon="trash" :description="t.trash.description">
    <div class="space-trash-cleanup" role="region" :aria-label="t.trash.region">
      <ConflictNote v-if="conflict" :text="t.trash.conflict" :reload-label="t.trash.reload" :busy="loading || saving" data-trash-cleanup-conflict @reload="reloadAfterConflict" @dismiss="conflict = false" />
      <ErrorState v-if="loadError && !saved" :title="t.trash.loadFailed" :text="loadError" :busy="loading" @retry="load" />
      <LoadingNote v-else-if="loading && !saved" :label="t.trash.loading" />
      <template v-else-if="saved">
        <div class="space-trash-cleanup-stats" data-trash-cleanup-counts>
          <StatTile :label="t.trash.inTrash" :value="String(saved.counts.trashed)" icon="trash">{{ itemsText(saved.counts.trashed) }}</StatTile>
          <StatTile :label="t.trash.due" :value="String(saved.counts.trashedDue)" icon="triangle-alert">{{ t.trash.olderThan(saved.effectiveDays) }}</StatTile>
          <StatTile :label="t.trash.unused" :value="String(saved.counts.unusedDue)" icon="archive">{{ t.trash.willTrash }}</StatTile>
        </div>
        <div v-if="context.openTrash && saved.counts.trashed > 0"><Button variant="outline" data-trash-open @click="context.openTrash(spaceId)">{{ t.trash.showTrash }}</Button></div>
        <SettingsSection :title="t.trash.rule" icon="clock">
          <p v-if="saved.decidedByWiki" class="space-trash-cleanup-note" data-trash-cleanup-wiki>
            {{ ruleText }} {{ t.trash.wikiRule }}
          </p>
          <template v-else>
            <SettingsRow :label="t.trash.cleanup" :label-for="false" :help="t.trash.cleanupHelp">
              <OptionPills v-model="mode" :options="modes" :label="t.trash.cleanup" hide-label :disabled="saving" data-trash-cleanup-mode />
            </SettingsRow>
            <SettingsRow v-if="mode === 'auto'" v-slot="{ id, describedBy, invalid }" :label="t.trash.deleteAfter" :help="t.trash.daysHelp(minCleanupDays, maxCleanupDays)" :error="dayProblem">
              <Input :id="id" v-model="days" type="number" inputmode="numeric" :min="minCleanupDays" :max="maxCleanupDays" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="saving" data-trash-cleanup-days @keydown.enter.prevent="save" />
            </SettingsRow>
          </template>
        </SettingsSection>
        <SettingsSection :title="t.trash.cleanNow" icon="trash">
          <p class="space-trash-cleanup-note">{{ ruleText }} {{ t.trash.cleanNowNote }}</p>
          <div><Button variant="outline" :disabled="dirty" data-trash-cleanup-run @click="ask">{{ t.trash.cleanNow }}</Button></div>
        </SettingsSection>
        <SaveBar v-if="!saved.decidedByWiki" :dirty="dirty" :saving="saving" :error="saveError || undefined" :save-label="t.trash.saveRule" @save="save" @cancel="reset" />
      </template>
    </div>
    <ConfirmDialog v-if="saved" :open="confirming" :title="t.trash.confirmTitle" :confirm-label="t.trash.confirm" :busy="running" :error="runError || undefined" data-trash-cleanup-dialog @confirm="run" @cancel="confirming = false">
      <p data-trash-cleanup-confirm>
        {{ t.trash.confirmText(itemsText(promised.due), promised.days, objectsText(promised.unused)) }}
      </p>
    </ConfirmDialog>
  </SettingsPage>
</template>

<style scoped>
.space-trash-cleanup { display: grid; gap: 16px; min-width: 0; }
.space-trash-cleanup-stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 12px; }
.space-trash-cleanup-note { margin: 0; color: var(--muted); font-size: 13.5px; }
</style>
