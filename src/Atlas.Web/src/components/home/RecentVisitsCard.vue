<script setup lang="ts">
import { inject, onBeforeUnmount, ref, shallowRef } from "vue";
import DashboardCard from "./DashboardCard.vue";
import HomePageRow from "./HomePageRow.vue";
import { errorMessage } from "../../api";
import { homeApi, homeApiKey } from "./homeApi";
import { listKeys } from "../../keyboard/navigation";
import type { LibraryItem } from "../../personalLibrary";
import type { LibraryView } from "../../shellRoute";
import type { TreeNodeType } from "../../treeMove";
import type { DashboardCardDefinition } from "./dashboardCards";
import t from "../../i18n/components-home";

// Naposledy navštívené: the newest visits of Knihovna → Nedávné (the same list, the first six).
defineProps<{ card: DashboardCardDefinition }>();
const emit = defineEmits<{ select: [id: string, type: TreeNodeType]; navigate: [view: LibraryView] }>();
const source = inject(homeApiKey, homeApi);
const items = shallowRef<LibraryItem[]>([]);
const loaded = ref(false), loading = ref(false), error = ref("");
const now = Date.now();
let abort: AbortController | undefined;

async function load() {
  abort?.abort();
  const controller = abort = new AbortController();
  loading.value = true; error.value = "";
  try {
    const result = await source.recent(controller.signal);
    if (!controller.signal.aborted) items.value = result.items;
  } catch (reason) {
    if (!controller.signal.aborted) error.value = errorMessage(reason);
  } finally {
    if (!controller.signal.aborted) { loading.value = false; loaded.value = true; }
  }
}
void load();
onBeforeUnmount(() => abort?.abort());
</script>

<template>
  <DashboardCard :title="card.title()" :icon="card.icon" :show-all="card.showAll" :loading="loading && !loaded" :error="error" :busy="loading"
    :empty="loaded && !items.length" :empty-title="t.recent.emptyTitle" :empty-text="t.recent.emptyHint" empty-icon="recent" :rows="6" row-height="52px"
    @retry="load()" @navigate="emit('navigate', $event)">
    <ul class="home-rows atlas-stagger" :aria-label="card.title()" @keydown="listKeys">
      <li v-for="item in items" :key="item.id"><HomePageRow :item="item" :at="item.visitedAt" :now="now" :label="t.recent.openPage(item.title)"
        @open="emit('select', $event.id, $event.type === 'board' ? 'board' : 'page')" /></li>
    </ul>
  </DashboardCard>
</template>

<style scoped>
.home-rows { list-style: none; margin: 0; padding: 0; }
.home-rows li + li { border-top: 1px solid var(--st-line-soft, var(--line)); }
</style>
