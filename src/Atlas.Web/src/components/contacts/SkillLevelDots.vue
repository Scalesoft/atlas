<script setup lang="ts">
/**
 * A level 1–5 as five dots in the topic's ink (`--tag-ink`, or `--skill-dots-ink` where the ink is another), filled up to the level: the
 * one drawing of a level for the person's line, the rating, a topic's people and the graph's list. Decorative (aria-hidden): the level is
 * said beside it in words or in the label. Why not the contacts plugin's dots: those are the reader card's own DOM, outside Vue.
 */
withDefaults(defineProps<{ level: number; size?: number }>(), { size: 5 });
</script>

<template>
  <span class="skill-level-dots" aria-hidden="true" :style="{ '--skill-dot': `${size}px` }"><i v-for="n in 5" :key="n" :class="{ on: n <= Math.round(level) }" /></span>
</template>

<style scoped>
.skill-level-dots { --ink: var(--skill-dots-ink, var(--tag-ink, var(--accent))); display: inline-flex; gap: 2px; }
.skill-level-dots i { width: var(--skill-dot); height: var(--skill-dot); border-radius: 999px; background: color-mix(in srgb, var(--ink) 22%, transparent); }
.skill-level-dots i.on { background: var(--ink); }
</style>
