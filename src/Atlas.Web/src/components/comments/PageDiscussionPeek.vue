<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from "vue";
import { errorMessage } from "../../api";
import { inertBlockHost, type BoardBlockHost } from "../../boards/editorBlocks";
import { focusWhenPresent } from "../../keyboard/navigation";
import { lazyComponent } from "../../lazyComponent";
import { drafts } from "../../lib/unsavedWork";
import type { DiscussionBubble } from "../../comments/pageDiscussionFeed";
import { themedUserColor } from "../../live/collaboratorColors";
import AtlasIcon from "../AtlasIcon.vue";
import SubjectAvatar from "../settings/SubjectAvatar.vue";
import t from "../../i18n/components-search";

// The quick reply's field is the page's comment field; its editor comes only when a reply is started.
const CommentComposer = lazyComponent(() => import("./CommentComposer.vue"));

/**
 * The quiet edge of a page's discussion, under the co-editors' avatars (the dock's Diskuse is the whole thread; this is its latest
 * words): each new comment of a colleague drops in as a bubble from the corner of the avatars, stays a few seconds (still while
 * it is pointed at or holds focus) and goes. A click, Enter on it or „Odpovědět“ opens the discussion with the caret in the field
 * (`open`). While the dock is shown (any tab) the host passes no bubbles and sets `docked`: the ones on screen fly to the right, toward
 * the dock, instead of fading up. Never both surfaces at once. Reduced motion keeps the fades only (the distances are tokens).
 * A live region, so a screen reader hears a new comment; the bubbles are buttons, so the keyboard reaches them (Alt C is the
 * shortcut into the discussion, this is its glance).
 *
 * With `reply` a bubble also has „Odpovědět“: a quick reply under it, the comment field of the page with the paper-plane (Enter sends,
 * Shift+Enter writes a line, Escape folds it). The bubble stays while it is written; once sent it flies off like the others at the dock.
 */
const props = withDefaults(defineProps<{
  bubbles: readonly DiscussionBubble[];
  docked?: boolean;
  ttl?: number;
  /** Sends a quick reply to the page's discussion; none: no reply here (the bubble only opens the discussion). */
  reply?: (markdown: string, bubble: DiscussionBubble) => Promise<void>;
  host?: BoardBlockHost;
  /** The page: a reply written here is kept as an unsent draft under it (it survives the bubble). */
  draftScope?: string;
}>(), { docked: false, ttl: 6000, reply: undefined, host: () => inertBlockHost, draftScope: "" });
const emit = defineEmits<{ open: []; dismiss: [id: string]; replying: [id: string, on: boolean] }>();

const timers = new Map<string, ReturnType<typeof setTimeout>>();
let held = false;
/** The bubble whose quick reply is open (it stays), the one being sent, the one that went with its reply, and the failure of a send. */
const replying = ref("");
const sending = ref(false);
const sent = ref("");
const failure = ref("");
function schedule(id: string) {
  if (held || timers.has(id) || id === replying.value) return;
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

const draftKey = (bubble: DiscussionBubble) => `comment-reply:${props.draftScope}:${bubble.id}`;
async function startReply(bubble: DiscussionBubble, event: Event) {
  const row = (event.currentTarget as HTMLElement).closest("li");
  replying.value = bubble.id;
  emit("replying", bubble.id, true);
  failure.value = "";
  clearTimeout(timers.get(bubble.id)); timers.delete(bubble.id);
  await nextTick();
  if (row) void focusWhenPresent(row, '[contenteditable="true"]', 90);
}
async function stopReply(bubble: DiscussionBubble) {
  replying.value = "";
  emit("replying", bubble.id, false);
  await nextTick();
  const row = document.querySelector(`[data-peek-bubble][data-bubble-id="${CSS.escape(bubble.id)}"]`);
  if (row) void focusWhenPresent(row, ".peek-reply", 30);
  schedule(bubble.id);
}
async function send(bubble: DiscussionBubble, markdown: string) {
  sending.value = true;
  failure.value = "";
  try {
    await props.reply!(markdown, bubble);
    sent.value = bubble.id;
    replying.value = "";
    emit("replying", bubble.id, false);
    await nextTick();
    drafts.delete(draftKey(bubble)); // the field kept its text as it went; a sent reply is no draft
    emit("dismiss", bubble.id);
    // The field went with the bubble: the keyboard lands on the discussion button the peek hangs under.
    document.querySelector<HTMLElement>("[data-page-discussion]")?.focus();
  } catch (error) {
    failure.value = errorMessage(error);
  } finally {
    sending.value = false;
  }
}
</script>

<template>
  <TransitionGroup tag="ol" name="peek" class="discussion-peek" :data-docked="docked || undefined" role="log" :aria-label="t.comments.peek.label" data-discussion-peek
    @pointerenter="hold" @pointerleave="release" @focusin="hold" @focusout="release">
    <li v-for="(bubble, index) in bubbles" :key="bubble.id" class="peek-bubble" :style="{ '--peek-index': index }" :data-bubble-id="bubble.id" :data-sent="sent === bubble.id || undefined" data-peek-bubble>
      <button type="button" class="peek-open" :aria-label="t.comments.peek.from(bubble.authorName, bubble.text)" @click="emit('open')">
        <SubjectAvatar kind="user" :name="bubble.authorName" :color="themedUserColor(bubble.authorId)" size="sm" />
        <span class="peek-main">
          <strong class="peek-author">{{ bubble.authorName }}</strong>
          <span class="peek-text">{{ bubble.text }}</span>
        </span>
      </button>
      <button v-if="reply && replying !== bubble.id" type="button" class="peek-reply" @click="startReply(bubble, $event)"><AtlasIcon name="quote" :size="13" />{{ t.comments.peek.reply }}</button>
      <button type="button" class="peek-hide" :aria-label="t.comments.peek.hide" :title="t.comments.peek.hide" @click="emit('dismiss', bubble.id)"><AtlasIcon name="close" :size="12" /></button>
      <div v-if="reply && replying === bubble.id" class="peek-compose" data-peek-reply>
        <CommentComposer :host="host" :busy="sending" :draft-key="draftKey(bubble)" cancelable enter-submits send-icon :placeholder="t.comments.peek.replyTo(bubble.authorName)" :aria-label="t.comments.peek.replyTo(bubble.authorName)" :hint="t.comments.hintEnter"
          @submit="send(bubble, $event)" @cancel="stopReply(bubble)" />
        <p v-if="failure" class="peek-failure" role="alert">{{ failure }}</p>
      </div>
    </li>
  </TransitionGroup>
</template>

<style scoped>
/* Under the avatars, over the page, out of the text column: the host's anchor is its positioning box. */
.discussion-peek { position: absolute; top: calc(100% + 8px); right: 0; z-index: 40; display: flex; flex-direction: column; gap: 6px; width: min(340px, 86vw); margin: 0; padding: 0; list-style: none; pointer-events: none; }
/* One bubble holds the comment, „Odpovědět“ and the quick reply: the frame is the row's, so nothing hangs outside its border. */
.peek-bubble { position: relative; pointer-events: auto; transform-origin: top right; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); box-shadow: var(--shadow-menu);
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .peek-bubble:has(> .peek-open:hover) { border-color: var(--accent); } .peek-open:hover { background: color-mix(in srgb, var(--hover) 45%, var(--surface)); } }
.peek-open { display: flex; align-items: center; gap: 10px; width: 100%; padding: 8px 30px 8px 10px; border: 0; border-radius: 11px; background: none; color: var(--text); font: inherit; text-align: left; cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), scale var(--atlas-motion-fast) var(--atlas-ease-out); }
.peek-open:active { scale: var(--atlas-press); }
.peek-open:focus-visible, .peek-hide:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.peek-main { display: flex; flex: 1; flex-direction: column; min-width: 0; font-size: 13px; line-height: 1.35; }
.peek-author { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.peek-text { display: -webkit-box; overflow: hidden; color: var(--muted); -webkit-box-orient: vertical; -webkit-line-clamp: 2; overflow-wrap: anywhere; }
/* „Odpovědět“ is a quiet text row under the bubble's text (in the flow, so the bubble never changes shape and nothing overlays the author line). */
.peek-reply { display: inline-flex; align-items: center; gap: 4px; margin: -4px 0 6px 10px; padding: 2px 6px; border: 0; border-radius: 6px; background: none; color: var(--accent); font: inherit; font-size: 12px; cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .peek-reply:hover { background: var(--hover); } }
.peek-reply:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.peek-compose { padding: 0 8px 8px; }
.peek-failure { margin: 6px 0 0; color: var(--error); font-size: 12px; }
.peek-hide { position: absolute; top: 6px; right: 6px; display: grid; place-items: center; width: 22px; height: 22px; padding: 0; border: 0; border-radius: 6px; background: none; color: var(--muted); cursor: pointer;
  opacity: 0; transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.peek-bubble:hover .peek-hide, .peek-bubble:focus-within .peek-hide { opacity: 1; }
@media (hover: hover) and (pointer: fine) { .peek-hide:hover { background: var(--hover); } }
@media not (pointer: fine) { .peek-hide { opacity: 1; } }

/* A colleague's comment drops in as a bubble from the corner of the avatars, one after another (the stagger of the list); it leaves up when
   its time runs out, and flies right toward the dock while the discussion opens (`data-docked`). Here, not in the eager motion.css: the peek
   is a lazy part. The distances are tokens and --st-pop-from is 1 under reduced motion: only the fades stay. */
.peek-enter-active { animation: atlas-peek-in var(--atlas-motion-pop) var(--atlas-ease-out) both; animation-delay: calc(var(--peek-index, 0) * var(--atlas-motion-base) / 5); }
.peek-leave-active { position: absolute; inset-inline: 0; animation: st-rise-out var(--atlas-motion-base) var(--atlas-ease-out) both; }
.peek-leave-active[data-sent], .discussion-peek[data-docked] .peek-leave-active { animation: atlas-peek-dock var(--atlas-motion-slow) var(--atlas-ease-in) both; animation-delay: calc(var(--peek-index, 0) * var(--atlas-motion-base) / 5); }
.peek-move { transition: transform var(--atlas-motion-base) var(--atlas-ease-inout); }
@keyframes atlas-peek-in {
  from { opacity: 0; transform: translate(calc(var(--atlas-rise) * 1.5), calc(var(--atlas-rise) * -2)) scale(var(--st-pop-from, .8)); }
  60% { opacity: 1; }
  to { opacity: 1; transform: none; }
}
@keyframes atlas-peek-dock {
  from { opacity: 1; transform: none; }
  to { opacity: 0; transform: translate(calc(var(--atlas-rise) * 10), 0) scale(var(--st-pop-from, .8)); }
}
</style>
