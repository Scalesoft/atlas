<script setup lang="ts">
import type { Appearance } from "../../api";

/**
 * A miniature window of the wiki in a theme and accent: navigation, a title line in the accent and two lines
 * of text (Já → Vzhled, the Motiv cards). Each half is an `.atlas[data-accent][data-mode]` island, so
 * palettes.css itself paints it with the real tokens and the dark glow; "system" shows light and dark side by
 * side. Colours fade when the accent changes.
 */
defineProps<{ mode: Appearance["mode"]; accent: Appearance["accent"] }>();
</script>

<template>
  <span class="theme-preview" :data-theme="mode" aria-hidden="true">
    <span v-for="half in mode === 'system' ? ['light', 'dark'] : [mode]" :key="half" class="atlas theme-preview-window" :data-accent="accent" :data-mode="half">
      <i class="theme-preview-nav"><b /><b /><b /></i>
      <span class="theme-preview-paper"><b /><b /><b /></span>
    </span>
  </span>
</template>

<style scoped>
.theme-preview { position: relative; display: block; width: 100%; height: 64px; overflow: hidden; border: 1px solid var(--line); border-radius: 8px; }
/* The islands are .atlas only to receive the palette; the shell's own .atlas layout does not apply to them. */
.atlas.theme-preview-window:not(.md-editor) {
  position: absolute;
  inset: 0;
  display: flex;
  min-height: 0;
  padding: 0;
  gap: 0;
  background: var(--surface);
  transition: background-color var(--atlas-motion-slow) var(--atlas-ease-out);
}
[data-theme="system"] > .theme-preview-window + .theme-preview-window { clip-path: inset(0 0 0 50%); }
.theme-preview-window[data-mode="dark"] { background: var(--atlas-glow, none), var(--surface); }
.theme-preview-nav { display: flex; flex-direction: column; gap: 5px; width: 30%; padding: 9px 7px; background: var(--sidebar-fill, var(--sidebar)); border-right: 1px solid var(--line); }
.theme-preview-nav > b { height: 5px; border-radius: 3px; background: color-mix(in srgb, var(--muted) 35%, transparent); }
.theme-preview-nav > b:first-child { width: 80%; background: var(--accent); }
.theme-preview-nav > b:last-child { width: 60%; }
.theme-preview-paper { display: flex; flex: 1; flex-direction: column; gap: 6px; padding: 10px; }
.theme-preview-paper > b { height: 5px; width: 88%; border-radius: 3px; background: var(--line); }
.theme-preview-paper > b:first-child { width: 62%; height: 8px; background: var(--accent); }
.theme-preview-paper > b:last-child { width: 58%; }
.theme-preview b, .theme-preview i { transition: background-color var(--atlas-motion-slow) var(--atlas-ease-out), border-color var(--atlas-motion-slow) var(--atlas-ease-out); }
</style>
