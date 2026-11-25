<script setup lang="ts">
import { computed, inject, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import { Button } from "../ui/button";
import AtlasIcon from "../AtlasIcon.vue";
import OptionPills from "../settings/OptionPills.vue";
import DashboardCard from "./DashboardCard.vue";
import ChangeRow from "./ChangeRow.vue";
import { errorMessage } from "../../api";
import { homeApi, homeApiKey, type ChangeFilter, type RecentChange } from "./homeApi";
import { rowKeys } from "../../keyboard/navigation";
import type { LibraryView } from "../../shellRoute";
import type { DashboardCardDefinition } from "./dashboardCards";
import t from "../../i18n/components-home";

// Co se změnilo: the newest change of each page the reader may read, Vše · Změny od AI · Moje stránky; „Načíst další“ follows the cursor.
const props = defineProps<{ card: DashboardCardDefinition }>();
const emit = defineEmits<{ select: [id: string, type: "page"]; navigate: [view: LibraryView]; history: [change: RecentChange] }>();
const source = inject(homeApiKey, homeApi);
const filter = ref<ChangeFilter>("all");
const items = shallowRef<RecentChange[]>([]);
const next = ref<string | null>(null);
const loaded = ref(false), loading = ref(false), error = ref("");
const options = computed(() => [
  { value: "all", label: t.changes.all },
  { value: "ai", label: t.changes.ai },
  { value: "mine", label: t.changes.mine },
]);
const emptyTitle = computed(() => filter.value === "ai" ? t.changes.emptyAi : filter.value === "mine" ? t.changes.emptyMine : t.changes.emptyTitle);
let abort: AbortController | undefined;

async function load(append = false) {
  abort?.abort();
  const controller = abort = new AbortController();
  loading.value = true; error.value = "";
  try {
    const result = await source.changes(filter.value, append ? next.value : null, controller.signal);
    if (controller.signal.aborted) return;
    items.value = append ? [...items.value, ...result.items.filter(item => !items.value.some(x => x.pageId === item.pageId))] : result.items;
    next.value = result.next;
  } catch (reason) {
    if (controller.signal.aborted) return;
    error.value = errorMessage(reason);
  } finally {
    if (!controller.signal.aborted) { loading.value = false; loaded.value = true; }
  }
}
// Another filter is another list: its skeleton (after the defer) instead of the rows of the previous one.
watch(filter, () => { loaded.value = false; items.value = []; next.value = null; void load(); });
void load();
onBeforeUnmount(() => abort?.abort());
</script>

<template>
  <DashboardCard :title="card.title()" :icon="card.icon" :loading="loading && !loaded" :error="items.length ? '' : error" :busy="loading"
    :empty="loaded && !items.length" :empty-title="emptyTitle" :empty-text="t.changes.emptyHint" empty-icon="history" :rows="6" row-height="60px"
    @retry="load()">
    <template #actions><OptionPills v-model="filter" class="changes-filter" :label="t.changes.filter" hide-label :options="options" /></template>
    <ul class="changes-list atlas-stagger" :aria-label="t.changes.title" @keydown="rowKeys($event, '.change-row')">
      <li v-for="change in items" :key="change.pageId"><ChangeRow :change="change" @open="emit('select', $event.pageId, 'page')" @history="emit('history', $event)" /></li>
    </ul>
    <div v-if="next || (error && items.length)" class="changes-more">
      <p v-if="error && items.length" class="changes-error" role="alert">{{ error }}</p>
      <Button variant="outline" size="sm" :disabled="loading" :aria-busy="loading" @click="load(true)"><AtlasIcon name="refresh" :size="15" />{{ t.changes.more }}</Button>
    </div>
  </DashboardCard>
</template>

<style scoped>
.changes-list { list-style: none; margin: 0; padding: 0; }
.changes-list li + li { border-top: 1px solid var(--st-line-soft, var(--line)); }
.changes-more { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 10px var(--st-pad-x, 18px); border-top: 1px solid var(--st-line-soft, var(--line)); }
.changes-error { margin: 0; font-size: 13px; color: var(--error, var(--danger, #b43a49)); }
.changes-filter :deep(.option-pills-row) { flex-wrap: nowrap; }
</style>
