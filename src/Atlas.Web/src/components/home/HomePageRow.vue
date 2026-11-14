<script setup lang="ts">
import AtlasIcon from "../AtlasIcon.vue";
import { formatAgo, formatDateTime } from "../../formatDate";
import { contentItemLink, isPlainNavigationClick } from "../../pageLinks";
import type { LibraryItem } from "../../personalLibrary";

// A page or board of the dashboard's short lists (Naposledy navštívené, the drafts of Čeká na mě): the compact form of Knihovna's row
// (PersonalLibraryPanel), which these cards link to with „Zobrazit vše“. Private to the dashboard.
const props = defineProps<{ item: LibraryItem; at?: string | null; label: string; now?: number }>();
const emit = defineEmits<{ open: [item: LibraryItem] }>();
function open(event: MouseEvent) {
  if (!isPlainNavigationClick(event)) return;
  event.preventDefault();
  emit("open", props.item);
}
</script>

<template>
  <a class="home-page-row" :href="contentItemLink(item)" :aria-label="label" :data-library-type="item.type ?? 'page'" @click="open">
    <AtlasIcon :name="item.type === 'board' ? 'layout-dashboard' : 'page'" :size="18" />
    <span class="home-page-copy"><strong :title="item.title">{{ item.title }}</strong><span :title="item.spaceName">{{ item.spaceName }}</span></span>
    <time v-if="at" :datetime="at" :title="formatDateTime(at)">{{ formatAgo(at, now) }}</time>
  </a>
</template>

<style scoped>
.home-page-row { display: flex; align-items: center; gap: 12px; min-height: 52px; padding: 8px var(--st-pad-x, 18px); color: var(--text); text-decoration: none; box-sizing: border-box; }
.home-page-row > svg { flex: 0 0 auto; color: var(--muted); }
.home-page-copy { display: grid; flex: 1; gap: 2px; min-width: 0; }
.home-page-copy > * { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.home-page-copy strong { font-size: 14px; font-weight: 600; }
.home-page-copy span { font-size: 12px; color: var(--muted); }
.home-page-row time { flex: 0 0 auto; font-size: 11px; color: var(--muted); white-space: nowrap; }
@media (hover: hover) and (pointer: fine) {
  .home-page-row:hover { background: var(--hover); }
}
.home-page-row:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
</style>
