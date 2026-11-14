<script setup lang="ts">
import { computed, ref } from "vue";
import ActivityFilters from "./ActivityFilters.vue";
import ActivityTimeline from "./ActivityTimeline.vue";
import SettingsSection from "./SettingsSection.vue";
import { activityFiltersFromQuery, activityFiltersToQuery, filterActivity, type ActivityFilterState, type ActivityItem } from "./activity";
import { useOptionalSettingsRoute } from "./useSettingsRoute";
import t from "../../i18n/settings-activity";
import { locale } from "../../i18n";

/**
 * A filterable "kdo co kdy": ActivityFilters over an ActivityTimeline. Inside a settings surface
 * the filters live in the URL (kdo/co/kdy/vysledek, no history entries), so a filtered view can be
 * linked; elsewhere they are local. Actors are taken from the items; categories name the kinds.
 */
const props = withDefaults(
  defineProps<{ items: ActivityItem[]; label: string; categories?: Record<string, { label: string; icon?: string }>; now?: Date }>(),
  { categories: () => ({}), now: () => new Date() },
);
const router = useOptionalSettingsRoute();
const local = ref<ActivityFilterState>({});
const filters = computed<ActivityFilterState>({
  get: () => (router ? activityFiltersFromQuery(router.route.value.query) : local.value),
  set: (value) => (router ? router.setQuery(activityFiltersToQuery(value)) : (local.value = value)),
});
const shown = computed(() => filterActivity(props.items, filters.value, props.now));
/** The empty state speaks of the filter only when one is set; with none, there is simply nothing yet. */
const filtering = computed(() => {
  const { who, what, when, result } = filters.value;
  return !!(who?.length || what || result || (when && when !== "all"));
});
/** Most active first: the filter offers the top few before anyone searches. */
const actors = computed(() => {
  const seen = new Map<string, { id: string; label: string; count: number }>();
  for (const item of props.items) {
    const actor = seen.get(item.actorId) ?? seen.set(item.actorId, { id: item.actorId, label: item.actor, count: 0 }).get(item.actorId)!;
    actor.count++;
  }
  return [...seen.values()].sort((a, b) => b.count - a.count || a.label.localeCompare(b.label, locale));
});
const kinds = computed(() => [...new Set(props.items.map((item) => item.category))].map((id) => ({ id, label: props.categories[id]?.label ?? id, icon: props.categories[id]?.icon })));
</script>

<template>
  <div class="activity-log">
    <ActivityFilters v-model="filters" :actors="actors" :categories="kinds" :total="items.length" :shown="shown.length" />
    <SettingsSection :label="label">
      <ActivityTimeline
        :items="shown"
        :now="now"
        :label="label"
        :empty-title="filtering ? t.log.noMatchTitle : t.log.emptyTitle"
        :empty-text="filtering ? t.log.noMatchText : t.log.emptyText"
      />
    </SettingsSection>
  </div>
</template>

<style scoped>
.activity-log { display: grid; gap: 14px; min-width: 0; }
</style>
