<script setup lang="ts">
import { computed, ref } from "vue";
import { spaceTrashApi, type TrashedSpace } from "../../../spaceSettings";
import { daysProblem, maxCleanupDays, minCleanupDays, wikiTrashCleanupApi, type WikiTrashCleanup, type WikiTrashCleanupApi } from "../../../trashCleanupApi";
import { Input } from "../../ui/input";
import ConflictNote from "../ConflictNote.vue";
import OptionPills from "../OptionPills.vue";
import SaveBar from "../SaveBar.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import { useDraftGuard } from "../dirtyGuard";
import { useSettingsFlash } from "../flash";
import { useSettingsDocument } from "../useSettingsDocument";
import { formatDateTimeSeconds } from "../../../formatDate";
import { tellTwins } from "../../../spaceTwins";
import ResultBanner from "../ResultBanner.vue";
import SettingsPage from "../SettingsPage.vue";
import TrashList from "../TrashList.vue";
import t from "../../../i18n/settings-global";
import { useSettingsContext } from "../context";
import { useTrashLoad } from "../useTrashLoad";

// Koš prostorů (unified trash spec §3): the spaces a wiki admin moved to the trash in Obecné, in the one trash list. Obnovit brings
// back the space with the pages trashed with it; Smazat trvale, after the name is typed, destroys it with its pages and vaults.
type Row = TrashedSpace & { title: string };
const props = withDefaults(defineProps<{ cleanupApi?: WikiTrashCleanupApi }>(), { cleanupApi: () => wikiTrashCleanupApi });
const context = useSettingsContext();
const flash = useSettingsFlash();
const result = ref("");
// After the last space goes, the focus lands on the outcome (UX3-4): the banner's close button.
const banner = ref<{ $el?: Element }>();
const { items, loading, loaded, error: loadError, load } = useTrashLoad<Row>(async signal =>
  (await spaceTrashApi.list(signal)).map(space => ({ ...space, title: space.name })));
async function restore(space: Row) {
  await spaceTrashApi.restore(space.id);
  result.value = t.trash.restored(space.name);
  context.spacesChanged();
}
async function purge(space: Row) {
  await spaceTrashApi.purge(space.id, space.name);
  result.value = t.trash.purged(space.name);
}
const twins = computed(() => tellTwins(items.value, space => space.name, () => undefined));
const pages = t.counts.pages;
const detail = (space: Row, index: number) => [twins.value[index], pages(space.pageCount), t.trash.trashedAt(formatDateTimeSeconds(space.trashedAt)), space.trashedByName].filter(Boolean).join(" · ");

// Čištění osiřelých prvků (spec §4b): either every space decides for itself (default) or this one rule holds everywhere.
const scope = ref("space"), mode = ref("manual"), days = ref("60");
const rule = useSettingsDocument<WikiTrashCleanup>({
  load: (signal) => props.cleanupApi.load(signal),
  save: (current) => props.cleanupApi.save({ scope: scope.value as WikiTrashCleanup["scope"], automatic: mode.value === "auto", days: Number(days.value) }, current.version),
  fill: (next) => { scope.value = next.scope; mode.value = next.automatic ? "auto" : "manual"; days.value = String(next.days); },
  // The rule is ignored while the spaces decide, so only the scope counts then.
  dirty: (next) => scope.value !== next.scope || (scope.value === "wiki" && (mode.value !== (next.automatic ? "auto" : "manual") || Number(days.value) !== next.days)),
  beforeSave: () => (scope.value === "wiki" && mode.value === "auto" ? daysProblem(Number(days.value)) : undefined),
  onSaved: () => flash.notify(t.trash.savedFlash, { icon: "trash" }),
  clearConflictOnSave: true,
  quietConflict: true,
});
const dayProblem = computed(() => (rule.dirty.value && scope.value === "wiki" && mode.value === "auto" ? daysProblem(Number(days.value)) : undefined));
const scopes = [{ value: "space", label: t.trash.scopeSpace, icon: "book-open" }, { value: "wiki", label: t.trash.scopeWiki, icon: "globe" }];
const modes = [{ value: "manual", label: t.trash.modeManual, icon: "user" }, { value: "auto", label: t.trash.modeAuto, icon: "clock" }];
function resetRule() { if (!rule.saving.value) rule.reset(); }
function saveRule() { if (rule.dirty.value) return rule.save(); }
useDraftGuard(rule.dirty, { save: saveRule, discard: resetRule });
void rule.load();
</script>

<template>
  <SettingsPage :title="t.trash.title" icon="trash" :description="t.trash.description">
    <SettingsSection :title="t.trash.cleanupTitle" icon="trash">
      <ConflictNote v-if="rule.conflict.value" :text="t.trash.conflict" :reload-label="t.trash.reload" :busy="rule.loading.value || rule.saving.value" data-trash-cleanup-conflict @reload="rule.reloadAfterConflict" @dismiss="rule.conflict.value = false" />
      <p v-if="rule.loadError.value" class="space-trash-rule-error" role="alert">{{ rule.loadError.value }} <button type="button" class="space-trash-retry" @click="rule.load">{{ t.trash.retry }}</button></p>
      <template v-else-if="rule.saved.value">
        <SettingsRow :label="t.trash.whoDecides" :label-for="false" :help="t.trash.whoDecidesHelp">
          <OptionPills v-model="scope" :options="scopes" :label="t.trash.whoDecides" hide-label :disabled="rule.saving.value" data-trash-cleanup-scope />
        </SettingsRow>
        <template v-if="scope === 'wiki'">
          <SettingsRow :label="t.trash.cleanup" :label-for="false" :help="t.trash.cleanupHelp">
            <OptionPills v-model="mode" :options="modes" :label="t.trash.cleanup" hide-label :disabled="rule.saving.value" data-trash-cleanup-mode />
          </SettingsRow>
          <SettingsRow v-if="mode === 'auto'" v-slot="{ id, describedBy, invalid }" :label="t.trash.days" :help="t.trash.daysHelp(minCleanupDays, maxCleanupDays)" :error="dayProblem">
            <Input :id="id" v-model="days" type="number" inputmode="numeric" :min="minCleanupDays" :max="maxCleanupDays" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="rule.saving.value" data-trash-cleanup-days @keydown.enter.prevent="saveRule" />
          </SettingsRow>
        </template>
        <SaveBar :dirty="rule.dirty.value" :saving="rule.saving.value" :error="rule.saveError.value || undefined" :save-label="t.trash.saveRule" @save="saveRule" @cancel="resetRule" />
      </template>
    </SettingsSection>
    <ResultBanner v-if="result" ref="banner" :message="result" @dismiss="result = ''" />
    <TrashList v-model:items="items" :label="t.trash.listLabel" :icon="space => space.icon" :detail="detail" :restore="restore" :purge="purge"
      :purge-title="t.trash.purgeTitle" :confirm-name="space => space.name" :loaded="loaded" :loading="loading" :error="loadError"
      :fallback="() => banner?.$el?.querySelector<HTMLElement>('button')"
      :empty-title="t.trash.emptyTitle" :empty-text="t.trash.emptyText" @retry="load">
      <template #purge="{ item }">
        <p>{{ t.trash.purgeBefore }} <strong>{{ item.name }}</strong> {{ t.trash.purgeAfter(pages(item.pageCount)) }}</p>
        <template v-if="item.vaultNames.length">
          <p>{{ t.trash.purgeVaults }}</p>
          <ul class="space-trash-vaults" data-space-trash-vaults><li v-for="vault in item.vaultNames" :key="vault"><strong>{{ vault }}</strong></li></ul>
        </template>
      </template>
    </TrashList>
  </SettingsPage>
</template>

<style scoped>
.space-trash-rule-error { margin: 0; color: var(--error); font-size: 13.5px; }
.space-trash-retry { background: none; border: 0; padding: 0; font: inherit; color: var(--accent); text-decoration: underline; cursor: pointer; }
.space-trash-vaults { margin: 0; padding-left: 20px; display: grid; gap: 4px; }
</style>
