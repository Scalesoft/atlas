<script setup lang="ts">
import { inject, onBeforeUnmount, ref, shallowRef } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import DashboardCard from "./DashboardCard.vue";
import HomePageRow from "./HomePageRow.vue";
import { errorMessage } from "../../api";
import { homeApi, homeApiKey } from "./homeApi";
import { listKeys } from "../../keyboard/navigation";
import { unreadNotifications } from "../../notifications";
import { buildShellHash, type LibraryView } from "../../shellRoute";
import { isPlainNavigationClick } from "../../pageLinks";
import type { LibraryItem } from "../../personalLibrary";
import type { DashboardCardDefinition } from "./dashboardCards";
import t from "../../i18n/components-home";

// Čeká na mě, from what exists: the unread count the sidebar badge already keeps (no request of its own) and the newest drafts with
// unpublished changes of Knihovna → Moje koncepty.
defineProps<{ card: DashboardCardDefinition }>();
const emit = defineEmits<{ select: [id: string, type: "page"]; navigate: [view: LibraryView] }>();
const source = inject(homeApiKey, homeApi);
const drafts = shallowRef<LibraryItem[]>([]);
const loaded = ref(false), loading = ref(false), error = ref("");
const inbox = buildShellHash({ view: "library", tab: "notifications" });
let abort: AbortController | undefined;

async function load() {
  abort?.abort();
  const controller = abort = new AbortController();
  loading.value = true; error.value = "";
  try {
    const result = await source.drafts(controller.signal);
    if (!controller.signal.aborted) drafts.value = result.items;
  } catch (reason) {
    if (!controller.signal.aborted) error.value = errorMessage(reason);
  } finally {
    if (!controller.signal.aborted) { loading.value = false; loaded.value = true; }
  }
}
function openInbox(event: MouseEvent) {
  if (!isPlainNavigationClick(event)) return;
  event.preventDefault();
  emit("navigate", "notifications");
}
void load();
onBeforeUnmount(() => abort?.abort());
</script>

<template>
  <DashboardCard :title="card.title()" :icon="card.icon" :show-all="card.showAll" :loading="loading && !loaded" :error="error" :busy="loading"
    :rows="4" row-height="52px" @retry="load()" @navigate="emit('navigate', $event)">
    <ul class="home-rows" :aria-label="card.title()" @keydown="listKeys">
      <li><a class="waiting-inbox" :href="inbox" data-waiting-inbox :data-unread="unreadNotifications || undefined" @click="openInbox">
        <AtlasIcon name="notifications" :size="18" /><span>{{ unreadNotifications ? t.waiting.unread(unreadNotifications) : t.waiting.noUnread }}</span>
        <AtlasIcon name="right" :size="16" /></a></li>
      <li class="waiting-heading">{{ drafts.length ? t.waiting.drafts : t.waiting.noDrafts }}</li>
      <li v-for="item in drafts" :key="item.id"><HomePageRow :item="item" :label="t.waiting.openDraft(item.title)"
        @open="emit('select', $event.id, 'page')" /></li>
    </ul>
  </DashboardCard>
</template>

<style scoped>
.home-rows { list-style: none; margin: 0; padding: 0; }
.home-rows li + li:not(.waiting-heading) { border-top: 1px solid var(--st-line-soft, var(--line)); }
.waiting-inbox { display: flex; align-items: center; gap: 12px; min-height: 52px; padding: 8px var(--st-pad-x, 18px); color: var(--text); text-decoration: none; font-size: 14px; box-sizing: border-box; }
.waiting-inbox > span { flex: 1; min-width: 0; }
.waiting-inbox > svg { flex: 0 0 auto; color: var(--muted); }
.waiting-inbox[data-unread] { font-weight: 600; }
.waiting-inbox[data-unread] > svg:first-child { color: var(--accent); }
.waiting-heading { padding: 12px var(--st-pad-x, 18px) 4px; font-size: 12px; font-weight: 650; color: var(--muted); border-top: 1px solid var(--st-line-soft, var(--line)); }
@media (hover: hover) and (pointer: fine) {
  .waiting-inbox:hover { background: var(--hover); }
}
.waiting-inbox:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
</style>
