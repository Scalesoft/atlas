<script setup lang="ts">
import { computed } from "vue";
import SettingsSection from "../settings/SettingsSection.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import { buildShellHash, type LibraryView } from "../../shellRoute";
import { isPlainNavigationClick } from "../../pageLinks";
import t from "../../i18n/components-home";

/**
 * One card of the home dashboard: SettingsSection (title, icon, `#actions`) with the states every card has, so a card is only its
 * data and rows. The body keeps the height of `rows` × `rowHeight` from the first frame, whatever state it shows, so nothing below
 * jumps when the data comes (show fast, signal loading). `loading` is the first load only; a reload keeps the rows.
 * `showAll`: the Knihovna list the card summarises, linked as „Zobrazit vše“.
 */
const props = withDefaults(defineProps<{
  title: string; icon?: string; showAll?: LibraryView; loading?: boolean; error?: string; empty?: boolean;
  emptyTitle?: string; emptyText?: string; emptyIcon?: string; rows?: number; rowHeight?: string; busy?: boolean;
}>(), { icon: undefined, showAll: undefined, error: "", emptyTitle: "", emptyText: undefined, emptyIcon: "info", rows: 4, rowHeight: "56px" });
const emit = defineEmits<{ retry: []; navigate: [view: LibraryView] }>();
const href = computed(() => props.showAll ? buildShellHash({ view: "library", tab: props.showAll }) : undefined);
function showAllClick(event: MouseEvent) {
  if (!props.showAll || !isPlainNavigationClick(event)) return;
  event.preventDefault();
  emit("navigate", props.showAll);
}
</script>

<template>
  <SettingsSection class="dashboard-card" :title="title" :icon="icon">
    <template v-if="$slots.actions || href" #actions>
      <slot name="actions" />
      <a v-if="href" class="dashboard-show-all" :href="href" :aria-label="t.showAllOf(title)" @click="showAllClick">{{ t.showAll }}</a>
    </template>
    <div class="dashboard-card-body" :style="{ '--dashboard-reserve': `calc(${rows} * ${rowHeight})` }" :aria-busy="loading || busy">
      <LoadingSkeleton v-if="loading" :rows="rows" :row-height="rowHeight" :label="t.loading(title)" />
      <ErrorState v-else-if="error" :title="t.loadFailed(title)" :text="error" :retry-label="t.retry" :busy="busy" @retry="emit('retry')" />
      <EmptyState v-else-if="empty" compact :icon="emptyIcon" :title="emptyTitle" :text="emptyText" />
      <slot v-else />
    </div>
  </SettingsSection>
</template>

<style scoped>
.dashboard-card { min-width: 0; }
/* The rows reach the card edges, as in a SettingsList; each row keeps the card inset itself. */
.dashboard-card > :deep(.settings-section-body) { padding: 0; gap: 0; }
.dashboard-card > :deep(.settings-section-header) { min-height: 56px; }
/* The title gives way to „Zobrazit vše“ and a filter until it would get shorter than a short title; then they wrap below it. */
.dashboard-card > :deep(.settings-section-header) > .settings-section-heading { flex: 1 1 8rem; min-width: 0; }
.dashboard-card-body { display: grid; align-content: start; min-height: var(--dashboard-reserve); }
.dashboard-card-body > :deep(.st-empty), .dashboard-card-body > :deep(.error-state) { margin: 14px var(--st-pad-x, 18px); }
/* The skeleton rows line up with the rows they stand for (each row keeps the card inset). */
.dashboard-card-body > :deep(.loading-skeleton) { padding-inline: calc(var(--st-pad-x, 18px) - 8px); box-sizing: border-box; }
.dashboard-show-all { flex: 0 0 auto; font-size: 13px; font-weight: 550; color: var(--accent); text-decoration: none; white-space: nowrap; border-radius: 6px; }
@media (hover: hover) and (pointer: fine) {
  .dashboard-show-all:hover { text-decoration: underline; }
}
</style>
