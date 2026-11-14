<script setup lang="ts">
import t from "../i18n/components-b";
import AtlasIcon from "./AtlasIcon.vue";
import CommandIcon from "./CommandIcon.vue";

/**
 * The page's actions at the thumb on a phone (refresh 2.5, board 13): Číst · Asistent · Oblíbené · Zámek. It only
 * gathers actions that also exist elsewhere (the top bar on wider screens, the ⋯ menu, the AI entries), so hiding it
 * never removes one. Shown below 681 px. The lock is the page's (3.6-F): a touch screen opens pages locked, so scrolling
 * never opens the keyboard; unlocking attaches the editor.
 */
withDefaults(defineProps<{
  /** The assistant is available (AI capability); otherwise its entry is not drawn at all. */
  canAsk?: boolean;
  /** The reader may write the page: the lock shows. */
  lockable?: boolean;
  locked?: boolean;
  favorite?: boolean;
  favoriteBusy?: boolean;
}>(), { canAsk: false, lockable: false, locked: false, favorite: false, favoriteBusy: false });
const emit = defineEmits<{ read: []; ask: []; favorite: []; lock: [] }>();
</script>

<template>
  <nav class="page-mobile-bar" :aria-label="t.mobile.actions">
    <button type="button" class="page-mobile-action" aria-current="page" @click="emit('read')"><AtlasIcon name="book" :size="20" /><span>{{ t.mobile.read }}</span></button>
    <button v-if="canAsk" type="button" class="page-mobile-action" @click="emit('ask')"><CommandIcon name="ai" :size="20" /><span>{{ t.mobile.assistant }}</span></button>
    <button type="button" class="page-mobile-action" :aria-pressed="favorite" :disabled="favoriteBusy" @click="emit('favorite')"><AtlasIcon name="star" :size="20" /><span>{{ t.mobile.favorites }}</span></button>
    <button v-if="lockable" type="button" class="page-mobile-action" :aria-pressed="locked" :aria-label="locked ? t.mobile.unlockEditing : t.mobile.lockEditing" data-page-mobile-lock @click="emit('lock')"><AtlasIcon :name="locked ? 'lock' : 'lock-open'" :size="20" /><span>{{ locked ? t.mobile.unlock : t.mobile.lock }}</span></button>
  </nav>
</template>

<style scoped>
.page-mobile-bar { display: none; }
/* Its place is one rule: the bottom of the screen, above the safe area. Under a long page it sticks there while the
   page scrolls and follows the last line when the end is reached, so it never covers it. Under a page shorter than
   the screen the view's leftover height lies above it (shell.css, the phone shell: the spacer is `order: 1`), so it
   sits at the bottom there too instead of hanging under the last line in the middle of the screen. */
@media (max-width: 680px) {
  .page-mobile-bar {
    position: sticky; bottom: 0; z-index: 4; order: 2;
    display: grid; grid-auto-columns: minmax(0, 1fr); grid-auto-flow: column; gap: 4px;
    height: 64px; padding: 4px 8px calc(8px + env(safe-area-inset-bottom, 0px));
    box-sizing: content-box;
    border-top: 1px solid color-mix(in srgb, var(--line) 80%, transparent); background: var(--surface);
  }
}
.page-mobile-action {
  display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 2px; min-width: 0;
  border: 0; border-radius: 10px; background: none; color: var(--muted); font: inherit; font-size: 11px; font-weight: 600; cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out);
}
.page-mobile-action[aria-current="page"] { background: color-mix(in srgb, var(--accent-soft) 70%, transparent); color: var(--accent); }
.page-mobile-action[aria-pressed="true"] { color: var(--accent); }
.page-mobile-action[aria-pressed] :deep(svg) { fill: transparent; transition: fill var(--atlas-motion-fast) var(--atlas-ease-out); }
.page-mobile-action[aria-pressed="true"]:not([data-page-mobile-lock]) :deep(svg) { fill: currentColor; }
.page-mobile-action:active:not(:disabled) { transform: scale(var(--atlas-press)); }
.page-mobile-action:focus-visible { outline: var(--focus-ring); outline-offset: -2px; }
.page-mobile-action:disabled { opacity: .55; cursor: default; }
</style>
