<script setup lang="ts">
import { computed, ref } from "vue";
import t from "../../../i18n/components-release";
import { useDemoLoop } from "./demoClock";

// The knowledge map in small: topics and the people who know them, each link as strong as the person's level. The graph draws itself,
// then „Kdo umí …“ asks about one topic after another: its links light up, a light runs along them to the people, and their level shows.
const d = t.demos.graph;
const TOPICS = [{ x: 62, y: 34 }, { x: 62, y: 72 }, { x: 62, y: 110 }];
const PEOPLE = [{ x: 232, y: 24 }, { x: 250, y: 60 }, { x: 226, y: 94 }, { x: 250, y: 126 }];
/** [topic, person, level 1–3] */
const LINKS: [number, number, number][] = [[0, 0, 3], [0, 2, 2], [1, 1, 3], [1, 0, 1], [1, 3, 2], [2, 3, 3], [2, 2, 1]];
const links = LINKS.map(([topic, person, level], index) => {
  const a = TOPICS[topic]!, b = PEOPLE[person]!, mid = (a.x + 44 + b.x - 12) / 2;
  return { topic, person, level, index, d: `M${a.x + 44} ${a.y}C${mid} ${a.y} ${mid} ${b.y} ${b.x - 12} ${b.y}` };
});
const root = ref<HTMLElement>();
const focus = ref(0);
const levels = computed(() => new Map(links.filter(link => link.topic === focus.value).map(link => [link.person, link.level])));

useDemoLoop(root, (at) => {
  [0, 1, 2].forEach((topic, index) => at(index * 2600, () => { focus.value = topic; }));
  return 7800;
}, () => { focus.value = 0; });
</script>

<template>
  <div ref="root" class="release-demo demo-graph" aria-hidden="true">
    <p class="demo-graph-ask"><span class="demo-graph-icon">⌕</span> <Transition name="demo-graph-swap" mode="out-in"><span :key="focus">{{ d.question(d.topics[focus]!) }}</span></Transition></p>
    <svg viewBox="0 0 300 140" class="demo-graph-svg">
      <g v-for="link in links" :key="link.index" class="demo-graph-link" :class="{ 'is-on': link.topic === focus }" :style="{ '--i': link.index, '--w': link.level }">
        <path class="demo-graph-edge" pathLength="1" :d="link.d" />
        <path class="demo-graph-light" pathLength="1" :d="link.d" />
      </g>
      <g v-for="(topic, index) in TOPICS" :key="`t${index}`" class="demo-graph-node demo-graph-topic" :class="{ 'is-on': index === focus }" :style="{ '--i': index }">
        <rect :x="topic.x - 46" :y="topic.y - 11" width="90" height="22" rx="11" />
        <text :x="topic.x - 1" :y="topic.y + 4" text-anchor="middle">{{ d.topics[index] }}</text>
      </g>
      <g v-for="(person, index) in PEOPLE" :key="`p${index}`" class="demo-graph-node demo-graph-person" :class="{ 'is-on': levels.has(index) }" :style="{ '--i': index + 3 }">
        <circle :cx="person.x" :cy="person.y" r="11" />
        <text :x="person.x" :y="person.y + 3.5" text-anchor="middle">{{ d.people[index]!.slice(0, 1) }}</text>
        <text class="demo-graph-level" :x="person.x + 15" :y="person.y + 4">{{ "★".repeat(levels.get(index) ?? 0) }}</text>
      </g>
    </svg>
  </div>
</template>

<style scoped>
.demo-graph { display: grid; grid-template-rows: auto 1fr; height: 100%; padding: 10px 12px 6px; }
.demo-graph-ask { display: flex; align-items: center; gap: 6px; width: fit-content; margin: 0; padding: 2px 10px; border: 1px solid var(--line); border-radius: 999px; background: var(--surface); font-size: 12px; font-weight: 600; }
.demo-graph-icon { color: var(--accent); }
.demo-graph-svg { width: 100%; height: 100%; min-height: 0; overflow: visible; }
.demo-graph-edge, .demo-graph-light { fill: none; stroke-linecap: round; }
.demo-graph-edge { stroke: color-mix(in srgb, var(--text) 22%, transparent); stroke-width: calc(var(--w) * .7px + .4px);
  animation: demo-graph-draw var(--atlas-motion-slow) var(--atlas-ease-out) backwards; animation-delay: calc(var(--i) * var(--atlas-motion-fast) / 2);
  transition: stroke var(--atlas-motion-base) var(--atlas-ease-out); }
.demo-graph-link.is-on .demo-graph-edge { stroke: color-mix(in srgb, var(--accent) 70%, transparent); }
/* A light runs along the links of the topic asked about, towards the people who know it. */
.demo-graph-light { stroke: var(--accent); stroke-width: 2.4px; stroke-dasharray: .1 .9; stroke-dashoffset: 1; opacity: 0; transition: opacity var(--atlas-motion-base) var(--atlas-ease-out); }
.demo-graph-link.is-on .demo-graph-light { opacity: 1; animation: demo-graph-run calc(var(--atlas-motion-slow) * 3) linear infinite; }
.demo-graph-node { transform-box: fill-box; transform-origin: center; animation: demo-graph-pop var(--atlas-motion-pop) var(--atlas-ease-out) backwards; animation-delay: calc(var(--i) * var(--atlas-motion-fast) / 2); }
.demo-graph-node rect, .demo-graph-node circle { fill: var(--surface); stroke: color-mix(in srgb, var(--text) 28%, transparent); stroke-width: 1; transition: stroke var(--atlas-motion-base) var(--atlas-ease-out), fill var(--atlas-motion-base) var(--atlas-ease-out); }
.demo-graph-node text { fill: var(--text); font: 600 10.5px system-ui, sans-serif; }
.demo-graph-node.is-on rect, .demo-graph-node.is-on circle { stroke: var(--accent); fill: color-mix(in srgb, var(--accent) 12%, var(--surface)); }
.demo-graph-level { fill: #f2b01e !important; font-size: 9px !important; letter-spacing: 1px; }
.demo-graph-swap-enter-active, .demo-graph-swap-leave-active { transition: opacity var(--atlas-motion-base) var(--atlas-ease-out); }
.demo-graph-swap-enter-from, .demo-graph-swap-leave-to { opacity: 0; }
@keyframes demo-graph-draw { from { stroke-dasharray: 1; stroke-dashoffset: 1; } to { stroke-dasharray: 1; stroke-dashoffset: 0; } }
@keyframes demo-graph-run { to { stroke-dashoffset: 0; } }
@keyframes demo-graph-pop { from { opacity: 0; scale: .6; } }
@media (prefers-reduced-motion: reduce) { .demo-graph-edge, .demo-graph-node { animation: none; } .demo-graph-light { display: none; } }
:global([data-atlas-reduced-motion] .demo-graph-edge), :global([data-atlas-reduced-motion] .demo-graph-node), :global([data-still] .demo-graph-edge), :global([data-still] .demo-graph-node) { animation: none; }
:global([data-atlas-reduced-motion] .demo-graph-light), :global([data-still] .demo-graph-light) { display: none; }
</style>
