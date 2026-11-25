<script setup lang="ts">
import { ref, watch } from "vue";
import AtlasIcon from "./AtlasIcon.vue";
import t from "../i18n/components-a";
import { Button } from "./ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "./ui/tooltip";

// The lock of a page's editing (Atlas 3.6, package F): a toggle button with an animated shackle. The page owns the state and
// answers an attempt to edit a locked page by raising `pulse`; a pulse asked for while one still runs is dropped, so a burst
// of keystrokes pulses once. Reduced motion swaps the pulse for a short colour and outline highlight.
// Locked, the lock reads „Jen pro čtení“ and a primary „Upravit“ stands beside it (owner 2026-11-07): the writer sees at a
// glance that the page is read-only and how to switch. Upravit is the same unlock (`toggle`); locking again stays on the lock.
// The toggle keeps one name, „Jen pro čtení“, and says its state with aria-pressed; on a phone it narrows to the icon.
const props = defineProps<{ locked: boolean; pulse?: number; shortcut?: string }>();
const emit = defineEmits<{ toggle: [] }>();

const pulsing = ref(false);
watch(() => props.pulse, () => { if (!pulsing.value) pulsing.value = true; });
</script>

<template>
  <Tooltip>
    <TooltipTrigger as-child>
      <Button type="button" variant="ghost" :size="locked ? 'sm' : 'icon'" class="edit-lock" :class="{ 'is-locked': locked, 'is-pulsing': pulsing }" :aria-pressed="locked" :aria-label="t.editLock.readOnly" data-edit-lock @click="emit('toggle')">
        <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path class="edit-lock-shackle" d="M8 11V7a4 4 0 0 1 8 0v4" />
          <rect x="4.5" y="11" width="15" height="10" rx="2" />
        </svg>
        <span v-if="locked" class="edit-lock-label">{{ t.editLock.readOnly }}</span>
        <span v-if="pulsing" class="edit-lock-ring" aria-hidden="true" @animationend="pulsing = false" />
      </Button>
    </TooltipTrigger>
    <TooltipContent>{{ locked ? t.editLock.unlock : t.editLock.lock }}<template v-if="shortcut"> ({{ shortcut }})</template></TooltipContent>
  </Tooltip>
  <Tooltip v-if="locked">
    <TooltipTrigger as-child>
      <Button type="button" size="sm" class="edit-unlock" :class="{ 'is-pulsing': pulsing }" data-edit-unlock @click="emit('toggle')"><AtlasIcon name="pencil" :size="15" />{{ t.editLock.edit }}</Button>
    </TooltipTrigger>
    <TooltipContent>{{ t.editLock.edit }}<template v-if="shortcut"> ({{ shortcut }})</template></TooltipContent>
  </Tooltip>
  <span class="sr-only" role="status" aria-live="polite">{{ pulsing ? t.editLock.pulse : "" }}</span>
</template>

<style scoped>
.edit-lock { position: relative; color: var(--muted); transition: color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out), transform var(--atlas-motion-fast) var(--atlas-ease-out); }
/* Locked it is a badge: neutral and framed, with words, so the state shows at a glance and never reads as the koncept tint. */
.edit-lock.is-locked { gap: 6px; color: var(--text); background: var(--bg-2); border: 1px solid var(--line); font-weight: 500; }
@media (max-width: 680px) { .edit-lock-label { display: none; } }
@media (hover: hover) and (pointer: fine) {
  .edit-lock:hover { background: var(--accent-soft); color: var(--accent); }
}
.edit-lock:active { transform: scale(var(--atlas-press)); }
/* A finger's target (P17 D5): the lock and Upravit are 44 px on a touch screen, where a board or page opens locked. */
@media (pointer: coarse) { .edit-lock { min-width: var(--touch-target, 44px); min-height: var(--touch-target, 44px); } .edit-unlock { min-height: var(--touch-target, 44px); } }
.edit-lock:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
/* The shackle is one shape in both states: closed it sits on the body, open it lifts and turns about its right foot. */
.edit-lock-shackle { transform-origin: 16px 11px; transition: transform var(--atlas-motion-base) var(--atlas-ease-out); transform: translateY(-2px) rotate(-22deg); }
.edit-lock.is-locked .edit-lock-shackle { transform: none; }
/* The ring lies over the badge and outside the layout flow; the badge and Upravit pop twice with it ("here, switch"). */
.edit-lock-ring { position: absolute; inset: 0; border-radius: inherit; border: 2px solid var(--accent); opacity: 0; pointer-events: none; animation: atlas-edit-lock-ring var(--atlas-motion-pop) var(--atlas-ease-out) 2; }
.edit-lock.is-pulsing, .edit-unlock.is-pulsing { animation: atlas-edit-lock-pop var(--atlas-motion-pop) var(--atlas-ease-out) 2; }
@media (prefers-reduced-motion: reduce) {
  .edit-lock-shackle { transition: none; }
  .edit-lock.is-pulsing, .edit-unlock.is-pulsing { animation: none; }
  .edit-unlock.is-pulsing { outline: 2px solid var(--accent); outline-offset: 2px; }
  .edit-lock-ring { inset: -2px; border: 0; opacity: 1; animation: atlas-edit-lock-flash var(--atlas-motion-slow) linear 1; background: transparent; }
}
:global([data-atlas-reduced-motion] .edit-lock-shackle) { transition: none; }
:global([data-atlas-reduced-motion] .edit-lock.is-pulsing) { animation: none; }
:global([data-atlas-reduced-motion] .edit-unlock.is-pulsing) { animation: none; }
:global([data-atlas-reduced-motion] .edit-unlock.is-pulsing) { outline: 2px solid var(--accent); outline-offset: 2px; }
:global([data-atlas-reduced-motion] .edit-lock-ring) { inset: -2px; border: 0; opacity: 1; animation: atlas-edit-lock-flash var(--atlas-motion-slow) linear 1; }
</style>
