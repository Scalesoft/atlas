<script setup lang="ts">
import { computed } from "vue";
import FilterMenu from "../components/settings/FilterMenu.vue";
import OverviewLevelSlider from "../overview/OverviewLevelSlider.vue";
import type { ChoiceOption } from "../components/settings/types";
import t from "../i18n/projector";
import { stateIcons } from "./requirementLook";
import { requirementStateLabel } from "./requirementSummary";
import { requirementStates } from "./requirementTypes";
import { setViewPrefs, viewPrefs, type RequirementSort } from "./requirementViewMode";
import "../components/settings/settings.css";

/**
 * The page's view of its requirements in its summary (M4a): the detail level NONE – S – M in the summary's head (`part="level"`, the
 * slider every overview card has, OverviewLevelSlider.vue), the order and the state filter as two FilterMenus at the card's foot (`part="menus"`): the
 * filter acts in the document, the order is the card's sorted list (owner 2026-11-04). The reader's own choice for this page, kept in this
 * browser (requirementViewMode.ts); it never
 * changes the page. The summary card shows it; nothing else needs it.
 */
/** `parties`: who the page's requirements are of (party, „Bez určení“ ""), with their counts, for the „Kdo“ filter; it shows from two. */
const props = defineProps<{ pageId: string; part: "level" | "menus"; parties?: readonly ChoiceOption[] }>();
const view = computed(() => viewPrefs(props.pageId).value);

const sorts: ChoiceOption[] = [
  { value: "", label: t.viewBar.noSort, icon: "list-ordered" },
  { value: "state", label: t.viewBar.state, icon: "activity" },
  { value: "date", label: t.viewBar.date, icon: "calendar" },
  { value: "party", label: t.viewBar.party, icon: "users" },
  { value: "key", label: "ID", icon: "hash" },
];
const states: ChoiceOption[] = [{ value: "", label: t.viewBar.all, icon: "filter" },
  ...requirementStates.map(state => ({ value: state, label: requirementStateLabel(state).replace(/^./, letter => letter.toUpperCase()), icon: stateIcons[state] }))];
</script>

<template>
  <div v-if="part === 'level'" class="requirement-level settings-ui" data-requirement-view-bar>
    <OverviewLevelSlider kind="requirements" :scope="pageId" data-requirement-density-switch />
  </div>
  <span v-else class="requirement-view-menus settings-ui" data-requirement-view-menus>
      <FilterMenu :model-value="view.sort" :options="sorts" :label="t.viewBar.sort" icon="arrow-down-a-z" data-requirement-sort
        @update:model-value="sort => setViewPrefs(pageId, { sort: sort as RequirementSort })" />
      <FilterMenu :model-value="view.state" :options="states" :label="t.viewBar.state" icon="filter" data-requirement-state-filter
        @update:model-value="state => setViewPrefs(pageId, { state: state as typeof view.state })" />
      <FilterMenu v-if="(parties?.length ?? 0) > 1" multiple :values="[...view.parties]" :options="[...parties!]" :label="t.viewBar.who" icon="users"
        :all-label="t.viewBar.everyone" :search-label="t.viewBar.searchWho" data-requirement-party-filter
        @update:values="parties => setViewPrefs(pageId, { parties })" />
  </span>
</template>

<style scoped>
.requirement-view-menus { display: inline-flex; flex-wrap: wrap; gap: .375rem; }
</style>
