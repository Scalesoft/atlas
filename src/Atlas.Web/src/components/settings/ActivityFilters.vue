<script setup lang="ts">
import { computed } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import FilterMenu from "./FilterMenu.vue";
import SearchableTags from "./SearchableTags.vue";
import t from "../../i18n/settings-activity";
import { formatNumber } from "../../i18n";
import { outcomeLabels, periodLabels, type ActivityFilterState, type ActivityOutcome, type ActivityPeriod } from "./activity";
import type { ChoiceOption } from "./types";

/**
 * Kdo / Co / Kdy / Výsledek in one line, so the timeline stays on screen (owner, 2026-09-28: filters
 * must not take the room of the content). Co, Kdy and Výsledek are FilterMenu choice menus; Kdo can
 * be 100+ people, so it is a compact SearchableTags — several people as removable tags, the most
 * active offered first (order `actors` by activity) while the field has focus. Active filters repeat
 * as removable chips; the parent keeps the state in the URL.
 */
const props = withDefaults(
  defineProps<{
    actors: { id: string; label: string; count?: number }[];
    categories: { id: string; label: string; icon?: string }[];
    total?: number;
    shown?: number;
  }>(),
  { total: undefined, shown: undefined },
);
const filters = defineModel<ActivityFilterState>({ required: true });

function field<K extends keyof ActivityFilterState>(key: K) {
  return computed({
    get: () => (filters.value[key] as string | undefined) ?? "",
    set: (value: string) => {
      filters.value = { ...filters.value, [key]: value || undefined };
    },
  });
}
const what = field("what");
const when = field("when");
const result = field("result");

const who = computed({
  get: () => filters.value.who ?? [],
  set: (value: string[]) => {
    filters.value = { ...filters.value, who: value.length ? value : undefined };
  },
});
const actorOptions = computed<ChoiceOption[]>(() =>
  props.actors.map((actor) => ({ value: actor.id, label: actor.label, icon: actor.id === "system" ? "server" : "account", badge: actor.count === undefined ? undefined : formatNumber(actor.count) })),
);
const categoryOptions = computed<ChoiceOption[]>(() => [{ value: "", label: t.filters.all, icon: "scroll-text" }, ...props.categories.map((category) => ({ value: category.id, label: category.label, icon: category.icon }))]);
const periodIcons: Record<ActivityPeriod, string> = { "24h": "clock", "7d": "calendar", "30d": "calendar", all: "history" };
const periodOptions = computed<ChoiceOption[]>(() => (["all", "24h", "7d", "30d"] as ActivityPeriod[]).map((value) => ({ value: value === "all" ? "" : value, label: t.periodShort[value], icon: periodIcons[value] })));
const resultOptions = computed<ChoiceOption[]>(() => [
  { value: "", label: t.filters.any, icon: "filter" },
  ...(Object.entries(outcomeLabels) as [ActivityOutcome, (typeof outcomeLabels)[ActivityOutcome]][]).map(([value, outcome]) => ({ value, label: outcome.label, icon: outcome.icon, tone: outcome.tone })),
]);

const chips = computed(() => {
  const list: { key: keyof ActivityFilterState; label: string; icon: string }[] = [];
  const value = filters.value;
  if (value.who?.length) {
    const one = value.who.length === 1 ? (props.actors.find((actor) => actor.id === value.who![0])?.label ?? value.who[0]) : t.filters.people(value.who.length);
    list.push({ key: "who", icon: value.who.length === 1 ? "account" : "users", label: t.filters.chipWho(one) });
  }
  if (value.what) list.push({ key: "what", icon: "filter", label: t.filters.chipWhat(props.categories.find((category) => category.id === value.what)?.label ?? value.what) });
  if (value.when) list.push({ key: "when", icon: "calendar", label: t.filters.chipWhen(periodLabels[value.when]) });
  if (value.result) list.push({ key: "result", icon: outcomeLabels[value.result].icon, label: t.filters.chipResult(outcomeLabels[value.result].label) });
  return list;
});
function clear(key?: keyof ActivityFilterState) {
  if (!key) filters.value = {};
  else filters.value = { ...filters.value, [key]: undefined };
}
</script>

<template>
  <div class="activity-filters" role="search" :aria-label="t.filters.label">
    <div class="activity-filter-bar">
      <SearchableTags v-model="who" :options="actorOptions" :label="t.filters.who" :placeholder="t.filters.whoPlaceholder" :empty-text="t.filters.whoEmpty" compact class="activity-filter-who" data-filter="who" />
      <FilterMenu v-model="what" :options="categoryOptions" :label="t.filters.what" icon="scroll-text" data-filter="what" />
      <FilterMenu v-model="when" :options="periodOptions" :label="t.filters.when" icon="calendar" data-filter="when" />
      <FilterMenu v-model="result" :options="resultOptions" :label="t.filters.result" icon="filter" data-filter="result" />
      <p v-if="total !== undefined" class="activity-filter-count" aria-live="polite">
        <!-- The timeline pages in: the count is of what is loaded, not of the whole audit. -->
        {{ chips.length ? t.filters.loadedOf(shown ?? total, total) : t.filters.loaded(total) }}
      </p>
    </div>
    <Transition name="st-fade">
      <div v-if="chips.length" class="activity-filter-summary">
        <TransitionGroup name="st-fade" tag="ul" class="activity-filter-chips" :aria-label="t.filters.applied">
          <li v-for="chip in chips" :key="chip.key">
            <button type="button" class="activity-chip" :aria-label="t.filters.clearOne(chip.label)" @click="clear(chip.key)"><AtlasIcon :name="chip.icon" :size="13" />{{ chip.label }}<AtlasIcon name="close" :size="13" /></button>
          </li>
        </TransitionGroup>
        <button v-if="chips.length > 1" type="button" class="st-link activity-clear" @click="clear()"><AtlasIcon name="close" :size="13" />{{ t.filters.clearAll }}</button>
      </div>
    </Transition>
  </div>
</template>

<style scoped>
.activity-filters { display: grid; gap: 8px; min-width: 0; }
.activity-filter-bar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; min-width: 0; }
.activity-filter-who { flex: 1 1 240px; min-width: 200px; max-width: 360px; }
.activity-filter-summary { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; }
.activity-filter-count { margin: 0 0 0 auto; color: var(--muted); font-size: 12.5px; font-variant-numeric: tabular-nums; white-space: nowrap; }
.activity-filter-chips { display: flex; flex-wrap: wrap; gap: 6px; margin: 0; padding: 0; list-style: none; }
.activity-chip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 3px 8px;
  border-radius: 999px;
  color: var(--accent);
  background: var(--accent-soft);
  font: inherit;
  font-size: 12.5px;
  font-weight: 600;
  cursor: pointer;
  transition: background-color var(--atlas-motion-fast), transform var(--atlas-motion-fast) var(--atlas-ease-out);
}
@media (hover: hover) and (pointer: fine) {
  .activity-chip:hover { background: color-mix(in srgb, var(--accent) 18%, var(--accent-soft)); }
}
.activity-chip:active { transform: scale(var(--atlas-press)); }
.activity-chip:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.activity-clear { font-size: 12.5px; background: none; cursor: pointer; }
</style>
