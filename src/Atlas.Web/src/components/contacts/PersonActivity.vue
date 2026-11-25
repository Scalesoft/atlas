<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import { api, errorMessage, type Space } from "../../api";
import { homeApi, type RecentChange } from "../home/homeApi";
import ChangeRow from "../home/ChangeRow.vue";
import LoadMoreButton from "../LoadMoreButton.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import FilterMenu from "../settings/FilterMenu.vue";
import type { ChoiceOption } from "../settings/types";
import { rowKeys } from "../../keyboard/navigation";
import t from "../../i18n/contacts-ui";

/**
 * Aktivita of a person (an internal contact is its account, `directoryId`): Co se změnilo narrowed to that account's changes, newest
 * first, with its rows (ChangeRow) and „Načíst další“, filtered by space. The server decides what the viewer may read, as on the home
 * dashboard; nobody's reading is shown. Use it wherever a person's card stands in a detail (ContentDetail); the home card stays
 * RecentChangesCard. `source` is replaced by the gallery and tests.
 */
const props = withDefaults(defineProps<{ accountId: string; source?: { changes: typeof homeApi.changes; spaces: () => Promise<Pick<Space, "id" | "name" | "icon">[]> } }>(),
  { source: () => ({ changes: homeApi.changes, spaces: () => api<Space[]>("/spaces") }) });
const emit = defineEmits<{ open: [pageId: string] }>();

const space = ref("");
const spaces = shallowRef<Pick<Space, "id" | "name" | "icon">[]>([]);
const items = shallowRef<RecentChange[]>([]);
const next = ref<string | null>(null);
const loaded = ref(false), loading = ref(false), error = ref("");
const options = computed<ChoiceOption[]>(() => [{ value: "", label: t.activity.allSpaces, icon: "book" }, ...spaces.value.map(each => ({ value: each.id, label: each.name, icon: each.icon || "book" }))]);
let abort: AbortController | undefined;

async function load(append = false) {
  abort?.abort();
  const controller = abort = new AbortController();
  loading.value = true; error.value = "";
  try {
    const result = await props.source.changes("all", append ? next.value : null, controller.signal, 10, { author: props.accountId, space: space.value || undefined });
    if (controller.signal.aborted) return;
    // The dock has no page history to open a change in: the row opens the page.
    const rows = result.items.map(item => ({ ...item, canOpenHistory: false }));
    items.value = append ? [...items.value, ...rows.filter(item => !items.value.some(x => x.pageId === item.pageId))] : rows;
    next.value = result.next;
  } catch (reason) {
    if (controller.signal.aborted) return;
    error.value = errorMessage(reason);
  } finally {
    if (!controller.signal.aborted) { loading.value = false; loaded.value = true; }
  }
}
watch(() => [props.accountId, space.value], () => { loaded.value = false; items.value = []; next.value = null; void load(); }, { immediate: true });
void props.source.spaces().then(list => { spaces.value = list; }, () => { /* Without the spaces the list stays whole. */ });
onBeforeUnmount(() => abort?.abort());
</script>

<template>
  <section class="person-activity" :aria-label="t.activity.title" data-person-activity>
    <header class="person-activity-head">
      <h4>{{ t.activity.title }}</h4>
      <FilterMenu v-if="spaces.length > 1" v-model="space" :label="t.activity.space" :options="options" :search-label="t.activity.searchSpace" data-person-activity-space />
    </header>
    <ErrorState v-if="error && !items.length" :title="t.activity.failed" :text="error" :busy="loading" @retry="load()" />
    <LoadingSkeleton v-else-if="!loaded" :rows="3" row-height="60px" :label="t.activity.loading" />
    <EmptyState v-else-if="!items.length" compact icon="history" :title="t.activity.empty" :text="t.activity.emptyText" />
    <template v-else>
      <ul class="person-activity-list atlas-stagger" @keydown="rowKeys($event, '.change-row a')">
        <li v-for="change in items" :key="change.pageId"><ChangeRow :change="change" @open="emit('open', $event.pageId)" /></li>
      </ul>
      <LoadMoreButton v-if="next || error" :busy="loading" :failed="!!error" data-person-activity-more @click="load(true)" />
    </template>
  </section>
</template>

<style scoped>
.person-activity { display: grid; gap: 8px; min-width: 0; --st-pad-x: 6px; }
.person-activity-head { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px; }
.person-activity-head h4 { margin: 0; font-size: 13px; font-weight: 600; color: var(--muted); }
.person-activity-list { margin: 0; padding: 0; list-style: none; border: 1px solid color-mix(in srgb, var(--line) 80%, transparent); border-radius: 10px; background: var(--surface); }
.person-activity-list li + li { border-top: 1px solid var(--st-line-soft, var(--line)); }
.person-activity > :deep(button) { justify-self: start; }
</style>
