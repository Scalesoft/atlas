<script setup lang="ts">
import { ICON_STROKE_WIDTH, ICON_VIEWBOX } from "@atlas/markdown/icons";
import { boardToolIcons, type BoardToolIcon } from "./boardToolIcons";

/** A tool glyph of boardToolIcons.ts, drawn like AtlasIcon (same view box and stroke). */
defineProps<{ name: BoardToolIcon; size?: number }>();
</script>

<template>
  <!-- eslint-disable-next-line vue/no-v-html -- constant markup of boardToolIcons.ts -->
  <svg :viewBox="ICON_VIEWBOX" :width="size ?? 18" :height="size ?? 18" fill="none" stroke="currentColor" :stroke-width="ICON_STROKE_WIDTH"
    stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" :data-icon="name" v-html="boardToolIcons[name]" />
</template>
