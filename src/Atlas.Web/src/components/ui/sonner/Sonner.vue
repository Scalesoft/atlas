<!-- THE ONE EDITED COMPONENT IN THIS DIRECTORY.
     Everything else under components/ui is byte-identical to what
     `npx shadcn-vue add` produces, so an update overwrites it cleanly. This file
     is not: its generated icons come from `@radix-icons/vue`, a set nothing else
     in the product uses, and toasts sit beside content drawn from the shared
     registry that the markdown editor also draws from.
     After `shadcn-vue add sonner`, swap the icon import back to AtlasIcon. -->
<script lang="ts" setup>
import type { ToasterProps } from "vue-sonner"
import AtlasIcon from '../../AtlasIcon.vue'
import { Toaster as Sonner } from "vue-sonner"
import "vue-sonner/style.css"
import { cn } from "@/lib/utils"
import { toastHost } from "@/lib/toastHost"

const props = defineProps<ToasterProps>()
</script>

<template>
  <Teleport :to="toastHost" :disabled="!toastHost">
  <Sonner
    :class="cn('toaster group', props.class)"
    :style="{
      // Dark mode has a raised tone for what floats over the page (menus, popovers); light has none and keeps the surface.
      '--normal-bg': 'var(--raised, var(--surface))',
      '--normal-bg-hover': 'var(--hover)',
      '--normal-text': 'var(--text)',
      '--normal-border': 'var(--line)',
      '--normal-border-hover': 'var(--input-line)',
      '--border-radius': 'var(--radius)',
    }"
    v-bind="props"
  >
    <template #success-icon>
      <AtlasIcon name="check" :size="16" />
    </template>
    <template #info-icon>
      <AtlasIcon name="circle-help" :size="16" />
    </template>
    <template #warning-icon>
      <AtlasIcon name="triangle-alert" :size="16" />
    </template>
    <template #error-icon>
      <AtlasIcon name="octagon-alert" :size="16" />
    </template>
    <template #loading-icon>
      <div>
        <AtlasIcon name="refresh" :size="16" class="toast-spinner" />
      </div>
    </template>
    <template #close-icon>
      <AtlasIcon name="close" :size="16" />
    </template>
  </Sonner>
  </Teleport>
</template>

<!-- vue-sonner brings its own timings (toast 400 ms, fade 300/200, spinner 1.2 s) and switches everything off under
     reduced motion. They are rewritten here on the Atlas tokens, as the prototype's toast(): in over the base
     duration and 10 px from below, out over the surface duration and 6 px down (the distances are rise tokens, so
     reduced motion keeps only the fade, plan O11). The library itself stays unpatched. -->
<style>
/* The toasts sit above what the page keeps at its bottom edge: the phone's action bar (76 px + the safe area) and a board's zoom corner. */
@media (max-width: 760px) { :root:has(.page-mobile-bar) { --atlas-toast-bottom: calc(88px + env(safe-area-inset-bottom, 0px)); } }
:root:has(.board-canvas-corner) { --atlas-toast-bottom: 72px; }
/* A toast is one of the floating surfaces: the menu's shadow (the library's own is a light-scheme one and vanished on
   dark), and one focus ring, the application's outline, instead of the library's grey ring around it as well. */
[data-sonner-toaster] [data-sonner-toast][data-styled='true'],
[data-sonner-toaster] [data-sonner-toast][data-styled='true']:focus-visible { box-shadow: var(--shadow-menu); }
[data-sonner-toaster] [data-sonner-toast][data-styled='true'] :is([data-close-button], [data-button]):focus-visible { box-shadow: none; }
/* Its close button is drawn in the toast's own colours in both schemes (the library does that only for dark and used
   its neutral greys in light: a #ededed ring on a toast outlined in --line), and its action has the radius of a Button
   of the same height (size xs) instead of 4 px. */
[data-sonner-toaster][data-sonner-theme] [data-sonner-toast][data-styled='true'] [data-close-button] {
  background: var(--normal-bg);
  border-color: var(--normal-border);
  color: var(--normal-text);
  transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out), border-color var(--atlas-motion-fast) var(--atlas-ease-out);
}
/* Atlas icons carry data-icon, which the library's toast-icon margin (-3px / 4px) also matches: it pushed the × 3.5 px left of the circle's centre. */
[data-sonner-toast][data-styled='true'] [data-close-button] > [data-icon] { margin: 0; }
@media (hover: hover) and (pointer: fine) { [data-sonner-toaster][data-sonner-theme] [data-sonner-toast][data-styled='true'] [data-close-button]:hover { background: var(--normal-bg-hover); border-color: var(--normal-border-hover); } }
[data-sonner-toaster] [data-sonner-toast][data-styled='true'] [data-button] {
  border-radius: var(--border-radius);
  transition: opacity var(--atlas-motion-base) var(--atlas-ease-out);
}
[data-sonner-toaster] { transition: transform var(--atlas-motion-base) var(--atlas-ease-out); }
[data-sonner-toast] {
  transition: transform var(--atlas-motion-base) var(--atlas-ease-out), opacity var(--atlas-motion-base) var(--atlas-ease-out), height var(--atlas-motion-base) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out);
}
[data-sonner-toast] > * { transition: opacity var(--atlas-motion-base) var(--atlas-ease-out); }
[data-sonner-toast][data-y-position='bottom']:not([data-mounted='true']) { --y: translateY(calc(var(--atlas-rise) * 1.25)); }
[data-sonner-toast][data-y-position='top']:not([data-mounted='true']) { --y: translateY(calc(var(--atlas-rise) * -1.25)); }
[data-sonner-toast][data-removed='true'][data-front='true'][data-swipe-out='false'] { --y: translateY(calc(var(--lift) * var(--atlas-rise) * -.75)); }
[data-sonner-toast][data-removed='true'][data-front='false'][data-swipe-out='false'][data-expanded='false'] {
  --y: translateY(calc(var(--atlas-rise) * .75));
  transition: transform var(--atlas-motion-surface) var(--atlas-ease-out), opacity var(--atlas-motion-surface) var(--atlas-ease-out);
}
[data-sonner-toast][data-removed='true'] { transition-duration: var(--atlas-motion-surface); }
[data-sonner-toast][data-swiping='true'] { transition: none; }
[data-sonner-toast][data-swipe-out='true'] { animation-duration: var(--atlas-motion-surface); animation-timing-function: var(--atlas-ease-out); }
[data-sonner-toast][data-promise='true'] [data-icon] > svg { animation-duration: var(--atlas-motion-base); animation-timing-function: var(--atlas-ease-out); }
.sonner-loading-wrapper[data-visible='false'] { animation-duration: var(--atlas-motion-surface); animation-timing-function: var(--atlas-ease-out); }
.sonner-loader { transition: opacity var(--atlas-motion-surface) var(--atlas-ease-out), transform var(--atlas-motion-surface) var(--atlas-ease-out); }
.sonner-loading-bar { animation-duration: var(--atlas-motion-spin); }
@media (prefers-reduced-motion: reduce) {
  /* The library's blanket "none !important" snaps; a toast fades instead (tokens: no distance, linear). */
  [data-sonner-toaster] [data-sonner-toast] {
    transition: opacity var(--atlas-motion-base) var(--atlas-ease-out) !important;
  }
  [data-sonner-toaster] [data-sonner-toast] > * { transition: opacity var(--atlas-motion-base) var(--atlas-ease-out) !important; }
  [data-sonner-toaster] [data-sonner-toast][data-removed='true'] { transition-duration: var(--atlas-motion-surface) !important; }
}
</style>

<style scoped>
/* The spinner of a loading toast spins on the spin token and stands under reduced motion (it fades in after the defer). */
.toast-spinner { animation: atlas-spin var(--atlas-motion-spin) linear infinite; }
@media (prefers-reduced-motion: reduce) {
  .toast-spinner { animation: atlas-backdrop-in var(--atlas-motion-base) var(--atlas-ease-out) var(--atlas-motion-defer) both; }
}
[data-atlas-reduced-motion] .toast-spinner { animation: atlas-backdrop-in var(--atlas-motion-base) var(--atlas-ease-out) var(--atlas-motion-defer) both; }
</style>
