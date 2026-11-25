<script setup lang="ts">
import t from "../i18n/components-b";
import shell from "../i18n/shell";
import AtlasIcon from "./AtlasIcon.vue";
import CommandIcon from "./CommandIcon.vue";

/**
 * The page's actions at the thumb on a phone (refresh 2.5, board 13): Číst · Diskuse · Asistent · Oblíbené · Zámek. Diskuse carries the count of
 * the page's comments as the top bar's discussion button does (hidden on a phone): the unread ones, accented, while a colleague's are new; it
 * leads to the section under the content. It only
 * gathers actions that also exist elsewhere (the top bar on wider screens, the ⋯ menu, the AI entries), so hiding it
 * never removes one. Shown below 681 px. The lock is the page's (3.6-F): a touch screen opens pages locked, so scrolling
 * never opens the keyboard; unlocking attaches the editor.
 */
const props = withDefaults(defineProps<{
  /** The assistant is available (AI capability); otherwise its entry is not drawn at all. */
  canAsk?: boolean;
  /** The reader may write the page: the lock shows. */
  lockable?: boolean;
  locked?: boolean;
  favorite?: boolean;
  favoriteBusy?: boolean;
  /** The page's comment count; null: the page has no discussion and the entry is not drawn. */
  discussion?: number | null;
  /** A colleague's new comments not seen yet: the count shows them, accented. */
  unread?: number;
}>(), { canAsk: false, lockable: false, locked: false, favorite: false, favoriteBusy: false, discussion: null, unread: 0 });
const emit = defineEmits<{ read: []; ask: []; favorite: []; lock: []; discussion: [] }>();
const badge = () => props.unread || props.discussion || 0;
</script>

<template>
  <nav class="page-mobile-bar" :aria-label="t.mobile.actions">
    <button type="button" class="page-mobile-action" aria-current="page" @click="emit('read')"><AtlasIcon name="book" :size="20" /><span>{{ t.mobile.read }}</span></button>
    <button v-if="discussion !== null" type="button" class="page-mobile-action" :aria-label="shell.common.discussionCount(discussion, unread)" data-page-mobile-discussion @click="emit('discussion')">
      <span class="page-mobile-icon"><AtlasIcon name="quote" :size="20" /><Transition name="page-mobile-count" mode="out-in"><span v-if="badge()" :key="badge()" class="page-mobile-count discussion-count" :data-fresh="unread > 0 || undefined" aria-hidden="true">{{ badge() }}</span></Transition></span><span>{{ t.mobile.discussion }}</span></button>
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
/* The count sits over the icon's corner (as on the top bar's discussion button), so a new number never moves the label. */
.page-mobile-icon { position: relative; display: inline-flex; }
.page-mobile-count { position: absolute; top: -5px; left: calc(100% - 7px); }
.page-mobile-count-enter-active { animation: st-rise-in var(--atlas-motion-base) var(--atlas-ease-out) both; }
.page-mobile-count-leave-active { animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-out) both; }
</style>
