<script setup lang="ts">
import { computed, ref } from "vue";
import t from "../../i18n/contacts-ui";
import { foldText } from "../../lib/foldText";
import { topicUrl } from "../../contacts/contactLinking";
import { shownLevel, type ContactSkill } from "../../contacts/skillApi";
import SearchField from "../settings/SearchField.vue";

/**
 * A person's knowledge at a glance (skills spec §7, §7a): one bar per topic, its length the level the reader may see (1–5), grouped by the
 * broader topic it is part of (.NET: C#, ASP.NET; a broader topic the person knows heads its group), the groups by their best topic and
 * the rest under Další. Built for hundreds of topics: a group shows its best `perGroup` and „Zobrazit další“, the first `groupsShown`
 * groups show and the rest wait behind one button, and from `filterFrom` topics on a filter narrows them all at once (the groups open).
 * A topic leads to who else knows it. Bars rather than a radar: they read with any number of topics and at 375 px. Why not
 * ContactSkillLine's chips: those are the card's one line; this is the person's overview (the dock's detail, the graph's person).
 */
const props = withDefaults(defineProps<{ skills: readonly ContactSkill[]; name?: string; perGroup?: number; groupsShown?: number; filterFrom?: number }>(),
  { name: "", perGroup: 5, groupsShown: 6, filterFrom: 20 });
const filter = ref("");
const opened = ref(new Set<string>());
const allGroups = ref(false);
const rated = computed(() => props.skills.filter(skill => shownLevel(skill) != null));
const groups = computed(() => {
  const text = foldText(filter.value.trim());
  const heads = new Set(rated.value.map(skill => skill.group).filter(Boolean));
  const map = new Map<string, ContactSkill[]>();
  for (const skill of rated.value) {
    const key = skill.group ?? (heads.has(skill.name) ? skill.name : "");
    if (text && !foldText(skill.name).includes(text) && !foldText(key).includes(text)) continue;
    map.set(key, [...(map.get(key) ?? []), skill]);
  }
  const best = (items: ContactSkill[]) => Math.max(...items.map(skill => shownLevel(skill) ?? 0));
  return [...map.entries()].sort(([a, x], [b, y]) => (a === "" ? 1 : 0) - (b === "" ? 1 : 0) || best(y) - best(x) || y.length - x.length || a.localeCompare(b))
    .map(([key, items]) => ({ key, title: key || t.skills.otherGroup,
      items: [...items].sort((a, b) => Number(b.name === key) - Number(a.name === key) || (shownLevel(b) ?? 0) - (shownLevel(a) ?? 0) || a.name.localeCompare(b.name)) }));
});
const filtering = computed(() => !!filter.value.trim());
const visibleGroups = computed(() => (filtering.value || allGroups.value ? groups.value : groups.value.slice(0, props.groupsShown)));
const hiddenGroups = computed(() => groups.value.length - visibleGroups.value.length);
const single = computed(() => groups.value.length === 1);
const shown = (group: { key: string; items: ContactSkill[] }) => (filtering.value || opened.value.has(group.key) ? group.items : group.items.slice(0, props.perGroup));
function toggle(key: string) { const next = new Set(opened.value); if (next.has(key)) next.delete(key); else next.add(key); opened.value = next; }
</script>

<template>
  <div v-if="rated.length" class="skill-bars" role="group" :aria-label="t.skills.barsLabel(name)" data-skill-bars>
    <SearchField v-if="rated.length >= filterFrom" v-model="filter" :label="t.skills.filterSkills" :placeholder="t.skills.filterSkillsPlaceholder" class="skill-bars-filter" />
    <div class="skill-bars-groups">
      <section v-for="group in visibleGroups" :key="group.key" class="skill-bars-group">
        <h4 v-if="!single" class="skill-bars-title">{{ group.title }} <small>{{ group.items.length }}</small></h4>
        <a v-for="skill in shown(group)" :key="skill.tagId" class="skill-bars-row" :href="topicUrl(skill.name)"
          :aria-label="`${skill.name}: ${t.skills.levels[Math.round(shownLevel(skill) ?? 1) - 1]}`">
          <span class="skill-bars-name" :title="skill.name">{{ skill.name }}</span>
          <span class="skill-bars-track" aria-hidden="true"><span class="skill-bars-fill" :style="{ '--level': (shownLevel(skill) ?? 0) / 5 }" /></span>
          <span class="skill-bars-value" aria-hidden="true">{{ (shownLevel(skill) ?? 0).toLocaleString(undefined, { maximumFractionDigits: 1 }) }}</span>
        </a>
        <button v-if="!filtering && group.items.length > perGroup" type="button" class="skill-bars-more" :aria-expanded="opened.has(group.key)" @click="toggle(group.key)">
          {{ opened.has(group.key) ? t.skills.showLess : t.skills.showMore(group.items.length - perGroup) }}
        </button>
      </section>
    </div>
    <button v-if="hiddenGroups > 0" type="button" class="skill-bars-more" data-skill-bars-groups @click="allGroups = true">{{ t.skills.moreGroups(hiddenGroups) }}</button>
  </div>
</template>

<style scoped>
.skill-bars { display: grid; gap: 10px; min-width: 0; container-type: inline-size; }
.skill-bars-groups { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 280px), 1fr)); gap: 12px 20px; align-items: start; }
/* Hundreds of topics: groups out of view are not laid out (AGENTS.md, fast first). */
.skill-bars-group { display: grid; gap: 2px; min-width: 0; content-visibility: auto; contain-intrinsic-size: auto 160px; }
.skill-bars-title { display: flex; align-items: baseline; gap: 6px; margin: 0 0 2px; color: var(--muted); font-size: 11.5px; font-weight: 650; letter-spacing: .02em; text-transform: uppercase; }
.skill-bars-title small { font-weight: 500; letter-spacing: 0; text-transform: none; }
.skill-bars-row { display: grid; grid-template-columns: minmax(0, 9.5rem) minmax(40px, 1fr) 2.2em; align-items: center; gap: 8px; padding: 2px 4px; border-radius: 6px;
  color: var(--text); font-size: 12.5px; text-decoration: none; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.skill-bars-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.skill-bars-track { height: 8px; border-radius: 999px; background: color-mix(in srgb, var(--accent) 12%, var(--surface)); overflow: hidden; }
/* The bar grows from zero once, on the motion tokens; reduced motion keeps it still. */
.skill-bars-fill { display: block; height: 100%; border-radius: inherit; background: var(--accent); transform-origin: left; scale: var(--level) 1;
  animation: atlas-bar-grow var(--atlas-motion-slow) var(--atlas-ease-out) both; }
@media (prefers-reduced-motion: reduce) { .skill-bars-fill { animation: none; } }
:global([data-atlas-reduced-motion]) .skill-bars-fill { animation: none; }
.skill-bars-value { color: var(--muted); font-size: 11.5px; font-variant-numeric: tabular-nums; text-align: right; }
.skill-bars-more { justify-self: start; padding: 2px 4px; border: 0; border-radius: 6px; background: transparent; color: var(--accent); font: inherit; font-size: 12px; cursor: pointer; }
@media (hover: hover) and (pointer: fine) { .skill-bars-row:hover { background: var(--hover); } .skill-bars-more:hover { text-decoration: underline; } }
.skill-bars-row:focus-visible, .skill-bars-more:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
@container (max-width: 300px) { .skill-bars-row { grid-template-columns: minmax(0, 7rem) minmax(30px, 1fr) 2.2em; } }
</style>
