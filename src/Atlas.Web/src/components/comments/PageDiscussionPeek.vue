<script setup lang="ts">
import { onBeforeUnmount, watch } from "vue";
import type { DiscussionBubble } from "../../comments/pageDiscussionFeed";
import { userColor } from "../../live/objectPresence";
import AtlasIcon from "../AtlasIcon.vue";
import SubjectAvatar from "../settings/SubjectAvatar.vue";
import t from "../../i18n/components-search";

/**
 * The quiet edge of a page's discussion, under the co-editors' avatars (the dock's Diskuse is the whole thread; this is its latest
 * words): each new comment of a colleague drops in as a bubble from the corner of the avatars, stays a few seconds (still while
 * it is pointed at or holds focus) and goes. A click, Enter on it or „Odpovědět“ opens the discussion with the caret in the field
 * (`open`). While the discussion is open the host passes no bubbles and sets `docked`: the ones on screen fly to the right, toward
 * the dock, instead of fading up. Never both surfaces at once. Reduced motion keeps the fades only (the distances are tokens).
 * A live region, so a screen reader hears a new comment; the bubbles are buttons, so the keyboard reaches them (Alt C is the
 * shortcut into the discussion, this is its glance).
 */
const props = withDefaults(defineProps<{ bubbles: readonly DiscussionBubble[]; docked?: boolean; ttl?: number }>(), { docked: false, ttl: 6000 });
const emit = defineEmits<{ open: []; dismiss: [id: string] }>();

const timers = new Map<string, ReturnType<typeof setTimeout>>();
let held = false;
function schedule(id: string) {
  if (held || timers.has(id)) return;
  timers.set(id, setTimeout(() => { timers.delete(id); emit("dismiss", id); }, props.ttl));
}
function clear() { timers.forEach(timer => clearTimeout(timer)); timers.clear(); }
function hold() { held = true; clear(); }
function release() { held = false; props.bubbles.forEach(bubble => schedule(bubble.id)); }
watch(() => props.bubbles.map(bubble => bubble.id), ids => {
  ids.forEach(schedule);
  for (const id of [...timers.keys()]) if (!ids.includes(id)) { clearTimeout(timers.get(id)); timers.delete(id); }
}, { immediate: true });
onBeforeUnmount(clear);
</script>

<template>
  <TransitionGroup tag="ol" name="peek" class="discussion-peek" :data-docked="docked || undefined" role="log" :aria-label="t.comments.peek.label" data-discussion-peek
    @pointerenter="hold" @pointerleave="release" @focusin="hold" @focusout="release">
    <li v-for="(bubble, index) in bubbles" :key="bubble.id" class="peek-bubble" :style="{ '--peek-index': index }" data-peek-bubble>
      <button type="button" class="peek-open" :aria-label="t.comments.peek.from(bubble.authorName, bubble.text)" @click="emit('open')">
        <SubjectAvatar kind="user" :name="bubble.authorName" :color="userColor(bubble.authorId)" size="sm" />
        <span class="peek-main">
          <strong class="peek-author">{{ bubble.authorName }}</strong>
          <span class="peek-text">{{ bubble.text }}</span>
        </span>
        <span class="peek-reply"><AtlasIcon name="quote" :size="13" />{{ t.comments.peek.reply }}</span>
      </button>
      <button type="button" class="peek-hide" :aria-label="t.comments.peek.hide" :title="t.comments.peek.hide" @click="emit('dismiss', bubble.id)"><AtlasIcon name="close" :size="12" /></button>
    </li>
  </TransitionGroup>
</template>

<style scoped>
/* Under the avatars, over the page, out of the text column: the host's anchor is its positioning box. */
.discussion-peek { position: absolute; top: calc(100% + 8px); right: 0; z-index: 40; display: flex; flex-direction: column; gap: 6px; width: min(340px, 86vw); margin: 0; padding: 0; list-style: none; pointer-events: none; }
.peek-bubble { position: relative; pointer-events: auto; transform-origin: top right; }
.peek-open { display: flex; align-items: center; gap: 10px; width: 100%; padding: 8px 30px 8px 10px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); color: var(--text); font: inherit; text-align: left; cursor: pointer;
  box-shadow: 0 8px 24px color-mix(in srgb, var(--text) 14%, transparent);
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out), scale var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .peek-open:hover { border-color: var(--accent); background: color-mix(in srgb, var(--hover) 45%, var(--surface)); } }
.peek-open:active { scale: var(--atlas-press); }
.peek-open:focus-visible, .peek-hide:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.peek-main { display: flex; flex: 1; flex-direction: column; min-width: 0; font-size: 13px; line-height: 1.35; }
.peek-author { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.peek-text { display: -webkit-box; overflow: hidden; color: var(--muted); -webkit-box-orient: vertical; -webkit-line-clamp: 2; overflow-wrap: anywhere; }
/* The reply hint takes no room until the bubble is pointed at or focused; its place is reserved, so nothing re-wraps. */
.peek-reply { display: inline-flex; flex-shrink: 0; align-items: center; gap: 4px; color: var(--accent); font-size: 12px; opacity: 0; transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out); }
.peek-open:focus-visible .peek-reply { opacity: 1; }
@media (hover: hover) and (pointer: fine) { .peek-open:hover .peek-reply { opacity: 1; } }
@media not (pointer: fine) { .peek-reply { display: none; } }
.peek-hide { position: absolute; top: 6px; right: 6px; display: grid; place-items: center; width: 22px; height: 22px; padding: 0; border: 0; border-radius: 6px; background: none; color: var(--muted); cursor: pointer;
  opacity: 0; transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.peek-bubble:hover .peek-hide, .peek-bubble:focus-within .peek-hide { opacity: 1; }
@media (hover: hover) and (pointer: fine) { .peek-hide:hover { background: var(--hover); } }
@media not (pointer: fine) { .peek-hide { opacity: 1; } }

/* The bubble's entrance, exit and flight to the dock are motion.css's (.peek-enter-active, .peek-leave-active, [data-docked], .peek-move). */
</style>
