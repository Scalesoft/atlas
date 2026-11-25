<script setup lang="ts">
import AtlasIcon from "../AtlasIcon.vue";

// Nothing here yet: what this place is for and the next possible step (slot). `inline`: one muted line with an icon and no frame, the
// "no match" note inside a picker or a small card (title only; the owner sets its padding by class).
withDefaults(defineProps<{ title: string; text?: string; icon?: string; compact?: boolean; inline?: boolean }>(), { text: undefined, icon: "info", compact: false, inline: false });
</script>

<template>
  <p v-if="inline" class="st-empty-inline" role="status"><AtlasIcon :name="icon" :size="16" />{{ title }}</p>
  <div v-else class="st-empty" :class="{ 'is-compact': compact }" role="status">
    <span class="st-icon-tile st-empty-icon" data-tone="neutral"><AtlasIcon :name="icon" :size="compact ? 18 : 22" /></span>
    <strong>{{ title }}</strong>
    <p v-if="text">{{ text }}</p>
    <div v-if="$slots.default" class="st-empty-actions"><slot /></div>
  </div>
</template>

<style scoped>
/* :where keeps the specificity low, so the owner's padding or margin by class wins. */
:where(.st-empty-inline) { display: flex; align-items: center; gap: 8px; margin: 0; color: var(--muted); font-size: 13px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.st-empty { display: grid; justify-items: center; gap: 8px; padding: 30px 18px; text-align: center; border: 1px dashed var(--line); border-radius: var(--st-radius, var(--atlas-radius-lg)); animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.st-empty.is-compact { padding: 18px 14px; }
/* The dashed frame is half ink, and the line token of the dark schemes is only 1.36:1 against the page (35,44,56 on 11,15,20),
   so it vanished there. In dark it takes a stroke between the line and the field's border (about 2.2:1): still quiet, drawn. */
:global([data-mode="dark"] .st-empty) { border-color: color-mix(in oklab, var(--input-line) 55%, var(--line)); }
/* Self-contained icon tile, so an empty state looks the same in the wiki workspace and in settings. */
.st-empty-icon { display: grid; place-items: center; width: 44px; height: 44px; border-radius: var(--st-radius-sm, var(--atlas-radius-md)); color: var(--muted); background: var(--hover); }
.st-empty.is-compact .st-empty-icon { width: 36px; height: 36px; }
.st-empty strong { font-size: 14.5px; }
/* `pretty` keeps the last line from being a lone word („později.“, „členy.“) under a centred sentence. */
.st-empty p { margin: 0; max-width: 460px; color: var(--muted); font-size: 13px; line-height: 1.55; text-wrap: pretty; }
.st-empty-actions { display: flex; flex-wrap: wrap; justify-content: center; gap: 8px; margin-top: 4px; }
</style>
