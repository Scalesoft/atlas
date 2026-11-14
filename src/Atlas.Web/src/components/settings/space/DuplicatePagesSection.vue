<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from "vue";
import t from "../../../i18n/settings-space";
import { duplicateClient, type DuplicateClient, type DuplicatePair } from "../../../ai/reports";
import { errorMessage } from "../../../api";
import { collapseHeight, riseIn } from "../../../motion/aiMotion";
import { Button } from "../../ui/button";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import SettingsSection from "../SettingsSection.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import { disclosureHooks } from "../disclosure";
const props = withDefaults(defineProps<{ spaceId: string; client?: DuplicateClient }>(), { client: () => duplicateClient });
const rows = ref<DuplicatePair[]>([]);
const loading = ref(true);
const error = ref("");
const decisionError = ref("");
const busy = ref("");
const root = ref<HTMLElement>();
let request: AbortController | undefined;
let disposed = false;
let decisionGeneration = 0;
const key = (pair: DuplicatePair) => pair.pageId + ":" + pair.otherPageId;
async function load() {
  decisionGeneration++; busy.value = "";
  request?.abort();
  const current = request = new AbortController();
  loading.value = true; error.value = ""; decisionError.value = "";
  try {
    const result = await props.client.list(props.spaceId, current.signal);
    if (current.signal.aborted) return;
    rows.value = result.items;
    loading.value = false;
    await nextTick();
    if (root.value) riseIn([...root.value.querySelectorAll<HTMLElement>("[data-duplicate-pair]")]);
  } catch (failure) { if (!current.signal.aborted) error.value = errorMessage(failure); }
  finally { if (request === current) loading.value = false; }
}
function openOther(pair: DuplicatePair) { window.open(`#/pages/${encodeURIComponent(pair.otherPageId)}`, "_blank", "noopener"); }
async function dismiss(pair: DuplicatePair) {
  if (busy.value) return;
  const generation = decisionGeneration;
  const current = () => !disposed && generation === decisionGeneration;
  busy.value = key(pair); decisionError.value = "";
  try {
    await props.client.dismiss(props.spaceId, pair.pageId, pair.otherPageId);
    if (!current()) return;
    const row = root.value?.querySelector<HTMLElement>(`[data-duplicate-pair="${CSS.escape(key(pair))}"]`);
    const restoreFocus = row?.contains(document.activeElement) ?? false;
    const animation = row ? collapseHeight(row) : null;
    if (animation) await animation.finished.catch(() => undefined);
    if (!current()) return;
    rows.value = rows.value.filter(item => key(item) !== key(pair));
    await nextTick();
    if (restoreFocus) (root.value?.querySelector<HTMLElement>("[data-dismiss-duplicate]") ?? root.value)?.focus();
  } catch (failure) { if (current()) decisionError.value = errorMessage(failure); }
  finally { if (current()) busy.value = ""; }
}
watch(() => props.spaceId, load, { immediate: true });
onBeforeUnmount(() => { disposed = true; request?.abort(); });
</script>
<template>
  <div ref="root" tabindex="-1" class="duplicate-pages" data-duplicate-pages>
    <SettingsSection :title="t.duplicates.title" icon="copy" :description="t.duplicates.description">
      <ErrorState v-if="error" :title="t.duplicates.loadFailed" :text="error" :busy="loading" @retry="load" />
      <LoadingSkeleton v-else-if="loading" :label="t.duplicates.loading" />
      <template v-else>
        <Transition :css="false" v-bind="disclosureHooks">
          <p v-if="decisionError" role="alert" class="st-muted">{{ decisionError }} {{ t.duplicates.decisionFailed }}</p>
        </Transition>
        <SettingsList v-if="rows.length" :label="t.duplicates.pairs">
          <SettingsListRow v-for="pair in rows" :key="key(pair)" :title="pair.title" icon="copy" :data-duplicate-pair="key(pair)">
            <template #title><a :href="`#/pages/${pair.pageId}`" target="_blank" rel="noopener" data-duplicate-page-link>{{ pair.title }}</a><span class="duplicate-between" aria-hidden="true">↔</span><a :href="`#/pages/${pair.otherPageId}`" target="_blank" rel="noopener" data-duplicate-page-link>{{ pair.otherTitle }}</a></template>
            <template #description>{{ t.duplicates.similarity(Math.round(pair.similarity * 100)) }}</template>
            <template #actions><Button as-child variant="ghost" size="sm"><a :href="`#/pages/${pair.pageId}`" target="_blank" rel="noopener" @click="openOther(pair)">{{ t.duplicates.openBoth }}</a></Button><Button type="button" variant="outline" size="sm" :disabled="!!busy" data-dismiss-duplicate @click="dismiss(pair)">{{ t.duplicates.notDuplicate }}</Button></template>
          </SettingsListRow>
        </SettingsList>
        <Transition :css="false" v-bind="disclosureHooks"><EmptyState v-if="!rows.length" compact :title="t.duplicates.noneTitle" :text="t.duplicates.noneText" icon="circle-check" /></Transition>
      </template>
    </SettingsSection>
  </div>
</template>
<style scoped>
.duplicate-pages { min-width: 0; }
.duplicate-between { display: inline-block; color: var(--muted); margin: 0 10px; }
a { color: var(--accent); text-underline-offset: 3px; }
</style>
