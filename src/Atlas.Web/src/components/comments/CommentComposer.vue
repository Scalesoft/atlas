<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, shallowRef } from "vue";
import { MarkdownEditor } from "@atlas/markdown/vue";
import type { MarkdownCommand, MarkdownEditorController, MarkdownRichValue } from "@atlas/markdown/core";
import "@atlas/markdown/styles/base.css";
import "@atlas/markdown/themes/ocean.css";
import "@atlas/markdown/themes/forest.css";
import "@atlas/markdown/themes/midnight.css";
import { boardTextDocument, boardTextPlugins, inertBlockHost, type BoardBlockHost } from "../../boards/editorBlocks";
import { cleanCommentText } from "../../comments/commentThread";
import { editorChrome, editorOutline, useEditorTheme } from "../../editor/editorSetup";
import { markdownProjection } from "../../pageSourceDraft";
import { boardTextInput } from "../boards/text/boardMarkdownText";
import "../boards/text/boardTextSurface.css";
import { Button } from "../ui/button";
import t from "../../i18n/components-search";
import { focusWhenPresent } from "../../keyboard/navigation";
import { draftKeeper, drafts, holdUnsavedWork } from "../../lib/unsavedWork";
import { pragueNow, historyStampText } from "@atlas/markdown/core";

/**
 * The text field of a comment (CommentThread's own part): the page editor with a page's configuration and content plugins
 * (`boardTextPlugins`: `[[` links, YouTrack, `@` mentions when the host has people), so writing a comment is writing in a page.
 * Ctrl+Enter (⌘+Enter) sends; the Markdown sent is what a page saves, cleaned to what the server takes. After a send the host
 * calls `reset()`; `cancelable` adds „Zrušit“ (editing a comment, or folding a page's empty discussion), and Escape cancels too
 * (a draft with „Použít“, P3: it is discarded and the comment keeps what it was). The whole field is the editor's surface, so a click
 * anywhere in it writes. With `draftKey` (a new comment) the typed text counts as unsaved work and is kept under that key when the
 * field goes, to come back with the next field (UX-P-6), unless „Zrušit“ or Escape threw it away (the host drops a kept draft once
 * its comment is sent). With `enterSubmits` (a requirement's note, quick like 3.4's) Enter sends and Shift+Enter writes a new line; with
 * `dayChip` a chip „dnes ▾“ picks the day of the note (sent as the second argument of `submit`) and returns to today after each send.
 */
const props = withDefaults(defineProps<{
  initial?: string;
  placeholder?: string;
  submitLabel?: string;
  cancelable?: boolean;
  busy?: boolean;
  host?: BoardBlockHost;
  ariaLabel?: string;
  draftKey?: string;
  hint?: string;
  enterSubmits?: boolean;
  dayChip?: boolean;
}>(), { initial: "", placeholder: () => t.comments.placeholder, submitLabel: () => t.comments.send, cancelable: false, busy: false, host: () => inertBlockHost, ariaLabel: () => t.comments.text, draftKey: undefined,
  hint: () => t.comments.hint, enterSubmits: false, dayChip: false });
const emit = defineEmits<{ submit: [markdown: string, date?: string]; cancel: [] }>();
const today = pragueNow().date;
const day = ref(today);
const dayInput = ref<HTMLInputElement>();
const dayLabel = () => day.value === today ? t.comments.today : historyStampText({ date: day.value });

const theme = useEditorTheme();
const plugins = [...boardTextPlugins(props.host), boardTextInput];
const key = props.draftKey;
const draft = shallowRef<MarkdownRichValue>((key && (drafts.get(key) as MarkdownRichValue | undefined)) || boardTextDocument(props.initial));
const typed = () => cleanCommentText(markdownProjection(draft.value, plugins));
let discarded = false;
if (key) {
  const keep = draftKeeper();
  keep(key, undefined);
  const release = holdUnsavedWork(() => !discarded && !!typed().trim());
  onBeforeUnmount(() => { release(); if (!discarded && typed().trim()) keep(key, draft.value); });
}
function cancel() {
  discarded = true;
  emit("cancel");
}
function submit() {
  const text = typed();
  if (props.busy || !text.trim()) return;
  if (props.dayChip) emit("submit", text, day.value || today); else emit("submit", text);
}
function keydown(event: KeyboardEvent) {
  if (event.key === "Escape" && props.cancelable) {
    event.preventDefault();
    event.stopPropagation();
    cancel();
    return;
  }
  // A suggestion list that is shown takes Enter; a hidden one elsewhere in the card (the dependencies' search) does not.
  const listed = [...document.querySelectorAll(".md-suggestion-list")].some(list => list.getClientRects().length);
  const plain = props.enterSubmits && !event.shiftKey && !event.altKey && !listed;
  if (event.key !== "Enter" || !(event.ctrlKey || event.metaKey || plain) || event.isComposing) return;
  event.preventDefault();
  event.stopPropagation();
  submit();
}
const form = ref<HTMLFormElement>();
function reset() {
  // The writer stays in the field for the next comment (keyboard pass). The same editor empties (perf P7: no new editor per send);
  // shortcut: Ctrl+Z there can bring the sent text back (the controller has no way to drop its history); remount if that misleads.
  const active = document.activeElement;
  const writing = !!form.value && (form.value.contains(active) || !active || active === document.body);
  draft.value = boardTextDocument("");
  day.value = today;
  if (writing) void nextTick(() => form.value && focusWhenPresent(form.value, '[contenteditable="true"]'));
}
// A host's own format buttons (a requirement's note: B, I, a list) act on the field as its shortcuts do.
let controller: MarkdownEditorController<"rich"> | null = null;
function dispatch(command: MarkdownCommand) { controller?.dispatch(command); }
function created(instance: MarkdownEditorController<"rich">) { controller = instance; }
defineExpose({ reset, submit, dispatch });
</script>

<template>
  <form ref="form" class="comment-composer" data-comment-composer @submit.prevent="submit" @keydown.capture="keydown">
    <div class="comment-composer-field">
    <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="draft" profile="rich" variant="compact" :ariaLabel="ariaLabel" :plugins="plugins" :lexicon="host.lexicon" :placeholder="placeholder"
      :outline="editorOutline" v-bind="editorChrome" :show-block-add-button="false" :fullscreenable="false" @created="created"
      :class="[theme, 'atlas-document-editor', 'board-text', 'comment-composer-editor']" />
    </div>
    <div class="comment-composer-actions">
      <span v-if="dayChip" class="comment-composer-day">
        <button type="button" class="comment-composer-day-chip" :aria-label="t.comments.noteDay" data-comment-day @click="dayInput?.showPicker?.()">{{ dayLabel() }} ▾</button>
        <input ref="dayInput" v-model="day" type="date" :max="today" tabindex="-1" aria-hidden="true" class="comment-composer-day-input" />
      </span>
      <span class="comment-composer-hint">{{ hint }}</span>
      <Button v-if="cancelable" variant="ghost" size="sm" type="button" data-comment-cancel @click="cancel">{{ t.comments.cancel }}</Button>
      <Button size="sm" type="submit" :disabled="busy" :aria-busy="busy || undefined" data-comment-submit>{{ submitLabel }}</Button>
    </div>
  </form>
</template>

<style scoped>
.comment-composer { display: flex; flex-direction: column; gap: 8px; min-width: 0; }
.comment-composer-field { border: 1px solid var(--line); border-radius: 10px; background: var(--surface); font-size: 14px;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
.comment-composer-field:focus-within { border-color: var(--accent); box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent) 18%, transparent); }
/* The compact editor (no block handle, so no gutter overlapping the dock) draws its own paper; the field is the paper here. */
.comment-composer-field :deep(.comment-composer-editor.md-editor),
.comment-composer-field :deep(.comment-composer-editor.md-editor:focus-within) { border: 0; border-radius: 0; background: transparent; box-shadow: none; }
/* The compact variant is a fixed two lines; a comment grows with its text up to a limit, then scrolls. */
.comment-composer-field :deep(.comment-composer-editor .editor-body) { height: auto; max-height: min(50vh, 360px); padding: 0; font-size: inherit; overflow-y: auto; }
/* The editable surface fills the field (padding included): a click below the first line still lands in the text, not on a dead border box. */
.comment-composer-field :deep(.comment-composer-editor .ProseMirror) { box-sizing: border-box; min-height: 72px; padding: 9px 12px; cursor: text; }
/* The placeholder belongs to the empty field only, not to the empty line after Shift+Enter. */
.comment-composer-field :deep(.comment-composer-editor .ProseMirror p.is-empty:not(.is-editor-empty)::before) { content: none; }
.comment-composer-actions { display: flex; align-items: center; justify-content: flex-end; gap: 8px; }
.comment-composer-day { position: relative; }
.comment-composer-day-chip { border: 1px solid var(--line); border-radius: 999px; background: var(--surface); color: var(--muted); font-size: 12px; padding: 2px 10px; cursor: pointer; }
.comment-composer-day-input { position: absolute; inset: 0; opacity: 0; pointer-events: none; }
.comment-composer-hint { margin-right: auto; color: var(--muted); font-size: 12px; }
/* A touch screen has no Ctrl+Enter: there the button sends (UX PG4-5). */
@media not (pointer: fine) { .comment-composer-hint { display: none; } }
</style>
