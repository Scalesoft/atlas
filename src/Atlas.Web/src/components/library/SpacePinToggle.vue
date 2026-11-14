<script setup lang="ts">
import t from "../../i18n/components-library";
import type { Space } from "../../api";
import AtlasIcon from "../AtlasIcon.vue";
import { Tooltip, TooltipContent, TooltipTrigger } from "../ui/tooltip";

// The pin of one space in Knihovna, on a card's cover or at the end of a list row: a toggle for the account's
// own pin, or a lock when the space administrator pinned it for everyone. SpaceDirectory owns the request.
defineProps<{ space: Pick<Space, "id" | "name" | "pinned" | "pinPolicy">; pending?: boolean }>();
const emit = defineEmits<{ toggle: [event: MouseEvent] }>();
</script>

<template>
  <Tooltip v-if="space.pinPolicy === 'Required'">
    <TooltipTrigger as-child>
      <span class="space-pin is-locked" tabindex="0" data-pin-locked><AtlasIcon name="lock" :size="14" /><span class="sr-only">{{ t.pin.lockedHidden }}</span></span>
    </TooltipTrigger>
    <TooltipContent>{{ t.pin.locked }}</TooltipContent>
  </Tooltip>
  <Tooltip v-else>
    <TooltipTrigger as-child>
      <button
        type="button"
        class="space-pin"
        :aria-pressed="space.pinned"
        :aria-label="t.pin.pin(space.name)"
        :aria-disabled="pending || undefined"
        :aria-busy="pending || undefined"
        :data-pin="space.id"
        @click.stop="!pending && emit('toggle', $event)"
      >
        <AtlasIcon :name="pending ? 'loader-circle' : 'pin'" :size="14" :class="{ 'atlas-spinner': pending }" />
      </button>
    </TooltipTrigger>
    <TooltipContent>{{ space.pinned ? t.pin.unpinTip : t.pin.pinTip }}</TooltipContent>
  </Tooltip>
</template>

<style scoped>
.space-pin {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  padding: 0;
  border: 0;
  border-radius: 8px;
  background: color-mix(in srgb, var(--surface) 82%, transparent);
  color: var(--input-line, var(--muted));
  box-shadow: 0 1px 2px color-mix(in srgb, var(--text) 12%, transparent);
  cursor: pointer;
  transition: color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out), transform var(--atlas-motion-fast) var(--atlas-ease-out);
}
.space-pin > svg { rotate: 35deg; transition: rotate var(--atlas-motion-base) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) {
  .space-pin:hover:not([aria-disabled="true"]):not(.is-locked) { background: var(--surface); color: var(--accent); }
}
/* Four presses deep (.92); reduced motion makes --atlas-press 1, so the pin stays still. */
.space-pin:active:not([aria-disabled="true"]):not(.is-locked) { transform: scale(calc(1 - (1 - var(--atlas-press)) * 4)); }
.space-pin:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
/* Pinned: upright and filled in the accent (shape and fill, not only the hue). */
.space-pin[aria-pressed="true"] { color: var(--accent); }
.space-pin[aria-pressed="true"] > svg, .space-pin[aria-disabled] > svg { rotate: 0deg; }
/* The fill fades in while the pin turns upright (200 ms); `transparent`, not `none`, so the fill has something to fade from.
   Not while the request runs: the icon is then the spinner, an open arc that a fill would turn into a solid blob. */
.space-pin > svg :deep(path) { fill: transparent; transition: fill var(--atlas-motion-fast) var(--atlas-ease-out); }
.space-pin[aria-pressed="true"]:not([aria-disabled]) > svg :deep(path) { fill: currentColor; }
.space-pin[aria-disabled] { cursor: progress; }
.space-pin.is-locked { color: var(--accent); cursor: default; }
.space-pin.is-locked > svg { rotate: 0deg; }
@media (prefers-reduced-motion: reduce) {
  .space-pin, .space-pin > svg { transition-property: color, background-color; }
}
</style>
