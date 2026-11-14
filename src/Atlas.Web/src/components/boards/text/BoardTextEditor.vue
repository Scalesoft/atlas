<script setup lang="ts">
import t from "../../../i18n/boards";
import { computed, nextTick, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import { MarkdownEditor } from "@atlas/markdown/vue";
import { markdownOverlaysFor, type MarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from "@atlas/markdown/core";
import type { Editor } from "@tiptap/core";
import "@atlas/markdown/styles/base.css";
import "@atlas/markdown/themes/ocean.css";
import "@atlas/markdown/themes/forest.css";
import "@atlas/markdown/themes/midnight.css";
import { cleanBoardText, maxBoardLabel, maxBoardText, mergeBoardText } from "../../../boards/boardText";
import { formatNumber } from "../../../i18n";
import { clearEditorUndo, setEditorUndo } from "../../../boards/editorUndo";
import { draftKeeper, drafts } from "../../../lib/unsavedWork";
import { boardTextDocument as readMarkdown, boardTextPlugins, inertBlockHost, type BoardBlockHost } from "../../../boards/editorBlocks";
import { boardTextIntentions } from "../../../boards/boardIntentions";
import { createCollaborationPresence } from "../../../collaborationPresence";
import { editorChrome, editorOutline, useEditorTheme } from "../../../editor/editorSetup";
import { markdownProjection } from "../../../pageSourceDraft";
import { boardTextInput, flattenInlineMarkdown, keepBoardLinks, restingSpellcheck } from "./boardMarkdownText";
import type { TextBinding } from "./textBinding";
import { placeCaret, type EditPoint } from "../../../boards/boardCaret";
import "./boardTextSurface.css";

/**
 * Editing a board text in place, as in Notion (spec §3.4), exactly as a page is edited: the page editor in its rich profile with the
 * page's configuration (`editor/editorSetup.ts`: theme and classes for caret, selection and collaborator colours, the hidden chrome;
 * the page's content plugins, `boardTextPlugins`), so the selection bubble, the slash menu, `**`/`_`, Ctrl+B/I, `- ` lists, `# `
 * headings and `[[` page links behave as in a page; a card has no fixed formatting row, the selection bubble, `/` and the block
 * handle with its "+" format it as they do a page. Only the item being edited mounts an editor; every other text is `BoardMarkdown`, read and laid out the same.
 *
 * A board text is stored as Markdown: editing starts from the binding's Markdown read as a page reads Markdown
 * (`boardTextDocument`) and ends with the Markdown projection a page saves (`markdownProjection`). The value goes through a
 * `TextBinding` (see there): `read` when editing starts, `commit` once when it ends with a change; a collaborative binding later
 * swaps the string for a shared fragment with remote carets, without changing this component's callers. Esc and a click outside
 * end the editing (the spec's „Esc nebo klik mimo edici potvrdí“), once an open popup of the editor has taken its Esc; so does Enter
 * in an `inline` text (a connector's label, a frame's title), which keeps one line: block markup typed there is dropped on commit.
 * The text is cleaned to what the server takes (tabs, control and bidi characters; `cleanBoardText`). A text longer than `max` is
 * never cut silently (UX3-2): the editor says by how much it is over, saves nothing meanwhile and stays open on Esc or a click
 * outside; an editor taken away mid-edit keeps the full text as a draft (`draftKey`) instead of saving it cut.
 * A colleague's text that arrives while editing is merged at each save (`mergeBoardText`), never overwritten; edits of one place keep
 * both versions, which the editor shows and says above the text, and the first Esc after that keeps the editing open (COL2-9).
 * Emits `commit(markdown)` after the binding took the text, `cancel` when nothing changed. `finish()` ends editing from outside.
 *
 * One editor for reading and writing: with `active` false the same editor shows the text
 * read-only and follows the binding's text in place; `active` starts an editing in it (no new editor, no swapped element) and its end
 * (or `active` taken away) leaves it read-only again. Without the right to write (the host's `canWrite`) when the editing is taken
 * away, what was typed is kept as the draft (`draftKey`) instead of going nowhere. Mounted without `active` (a contact's field, a note
 * of the personal items) it edits from the start, as before.
 */
const props = withDefaults(defineProps<{
  binding: TextBinding;
  inline?: boolean;
  placeholder?: string;
  ariaLabel?: string;
  /** Host services of the page's plugins on this board (`[[` pages, YouTrack); none in the gallery. */
  host?: BoardBlockHost;
  /** The type's own limit of the stored text (a note's 100 000); a board's item text and one-line label otherwise. */
  max?: number;
  /**
   * false where the text is an entity's own field (a contact's name, function, address): no `@`, no people search, no contact chips,
   * so typing „Jana Nováková“ into a contact never mentions another one. Default true.
   */
  entities?: boolean;
  /** Where a text that is over its limit when the editing is taken away is kept (not cut) to come back with the next editor of it. */
  draftKey?: string;
  /** Edited now; false: the text read-only in the same editor, until the next editing. */
  active?: boolean;
  /** Where the editing was aimed (that word, that place); the end of the text without it. */
  at?: EditPoint;
}>(), { inline: false, ariaLabel: t.textEditor.label, host: () => inertBlockHost, entities: true, active: true });
/** `unsaved`: whether the editor holds typed text its binding's `save` has not yet received (the board says „Neuloženo“). */
const emit = defineEmits<{ commit: [markdown: string]; cancel: []; unsaved: [holding: boolean] }>();

const theme = useEditorTheme();
// A board text as the editor shows it: read as a page reads Markdown, without the links a board text may not keep.
const boardTextDocument = (markdown: string) => keepBoardLinks(readMarkdown(markdown));
const adapter = props.binding.collaboration;
const keep = draftKeeper();
// What one editing starts from (`start`): the binding's text as it was read, and a draft kept from an editing taken away.
let initial = adapter ? "" : props.binding.read();
let read = boardTextDocument(initial);
/** The Markdown the editor writes for the text as it was read (its own dialect: `_a_` as `*a*`); taken from the editor when it holds it. */
let readProjection: string | undefined;
let kept: string | undefined;
const draft = shallowRef<MarkdownRichValue>(read);
// The editor writes the same text its own way (`_a_` as `*a*`) and normalizes it as it loads (a trailing paragraph): a change is a
// local edit whose Markdown differs from that of the text as it was read.
let edited = false;
// No editing going on: at rest, or after it ended.
let done = true;
const controller = shallowRef<MarkdownEditorController<"rich"> | null>(null);
const root = ref<HTMLElement>();
// The collaborators' carets and colours of the page editor; a local text has none.
const presence = adapter ? createCollaborationPresence({ onChange: () => {} }) : undefined;
// The editor of this text, for the caret: editing starts at the end of the text, where a person continues it (not at its start).
let editor: Editor | undefined;
const caret: MarkdownPlugin = { id: "atlas-board-text-caret", apiVersion: 1, create: () => ({ onAttach: context => { editor = context.editor; } }) };
// The board's module rows (AI while it can answer) in this editor's „Co s tím jde dělat“ over the selection or the whole text (P11).
// Recognition reads the space's lexicon in board text, never inside an entity's own fields (no recursion).
const lexicon = props.entities ? props.host.lexicon : undefined;
const plugins = [...boardTextPlugins(props.host, { entities: props.entities }), boardTextInput, caret, restingSpellcheck(() => props.active), boardTextIntentions(() => current()), ...(presence ? [presence.plugin] : [])];
let unsubscribe: (() => void) | undefined;

// The board's „Zpět“ wedge follows this editor while it is edited (boards/editorUndo.ts).
const undoToken = {};
const undoQuery = { type: "undo" } as const;
function offerUndo() {
  const instance = controller.value;
  if (props.active && instance) setEditorUndo(undoToken, !!instance.query(undoQuery).enabled, () => { if (instance.dispatch(undoQuery)) instance.focus(); });
  else clearEditorUndo(undoToken);
}
watch(() => props.active, offerUndo);
function created(instance: MarkdownEditorController<"rich">) {
  controller.value = instance;
  instance.subscribe(offerUndo);
  unsubscribe = presence && instance.subscribe(state => presence.update(state.peers));
  if (props.active) focusEnd();
}
/**
 * The caret where the editing was aimed. A new item's node is hidden until the canvas has measured it, and a hidden editor refuses the
 * focus: it is tried again each frame (a second at most) until the editor holds it, while the editing goes on.
 */
function focusEnd(tries = 60) {
  if (!props.active || done) return;
  if (editor && placeCaret(editor, props.at) && editor.view.hasFocus()) return;
  if (tries > 0) requestAnimationFrame(() => focusEnd(tries - 1));
  else controller.value?.focus();
}
// A binding that saves while editing gets the text after this pause in typing.
const savePause = 1000;
let saveTimer: ReturnType<typeof setTimeout> | undefined;
/** The text the binding last received while editing; undefined before the first save. */
let saved: string | undefined;
let holding = false;
function hold(now: boolean) {
  if (holding !== now) emit("unsaved", holding = now);
}
function changed(_value: MarkdownRichValue, { origin }: { origin: "local" | "remote" }) {
  if (origin !== "local" || done) return;
  edited = true;
  clearTimeout(measureTimer);
  measureTimer = setTimeout(measure, measurePause);
  if (!props.binding.save) return;
  hold(true);
  clearTimeout(saveTimer);
  saveTimer = setTimeout(save, savePause);
}

// One line (a contact's name, a frame title): only the marks it keeps; headings, lists and blocks would only cover what is around it.
const inlineFormatting = ["bold", "italic", "strike", "code", "link"] as const;
const formatting = computed(() => (props.inline ? inlineFormatting : undefined));
const limit = computed(() => props.max ?? (props.inline ? maxBoardLabel : maxBoardText));
const clean = (raw: string, max = limit.value) => cleanBoardText(props.inline ? flattenInlineMarkdown(raw) : raw.trimEnd(), { line: props.inline, max });
const project = (value: MarkdownRichValue) => markdownProjection(value, boardTextPlugins(props.host, { entities: props.entities }));
// The editor's own projection of what it holds (no editor built for it); a detached projection only before the editor is there.
const current = () => (controller.value ? controller.value.getProjection().markdown : project(draft.value));
/** How many characters the edited text is over its limit; measured after a short pause in typing, and before every save. */
const over = ref(0);
const measurePause = 300;
let measureTimer: ReturnType<typeof setTimeout> | undefined;
function measure() {
  clearTimeout(measureTimer);
  over.value = edited ? Math.max(0, clean(current(), Infinity).length - limit.value) : 0;
  return over.value;
}
const overMessage = computed(() => t.textEditor.over(over.value, formatNumber(limit.value)));
/** The binding's text this editing last agreed with (as it was read, or as it last went out): a colleague's change differs from it. */
let base = initial;
/** Every text this editing read or sent: the binding may still show an earlier one of them (its echo arrives a render later). */
let own = new Set([initial]);
/** A colleague changed the same place meanwhile: both versions are in the text, said above it. */
const conflict = ref(false);
/**
 * What goes out for `text` (COL2-9): `text` itself, or, when a colleague's text arrived meanwhile, both merged
 * (`mergeBoardText`), never one overwriting the other. `send` undefined: the stored text already holds it all. While the editing
 * goes on (`shown`), the editor shows the merge.
 */
// shortcut: two saves that cross in flight (both sent within one round trip) are still ordered by the server, the later one winning;
// send the base text with a text `set` and let the server refuse a stale one if that window matters.
function outgoing(text: string, shown: boolean): { send?: string; conflict: boolean } {
  const theirs = adapter ? base : props.binding.read();
  if (own.has(theirs)) return { ...(text !== base && { send: text }), conflict: false };
  const normalized = (markdown: string) => clean(project(boardTextDocument(markdown)));
  const other = normalized(theirs);
  const merged = mergeBoardText(normalized(base), text, other);
  base = theirs;
  own.add(theirs);
  if (merged.conflict) conflict.value = true;
  if (shown && merged.text !== text) draft.value = boardTextDocument(merged.text);
  return { ...(merged.text !== other && { send: merged.text }), conflict: merged.conflict };
}
/** The edited text as stored, or undefined while it is still the text as it was read. */
function changedText() {
  if (!edited) return undefined;
  const text = clean(current());
  return text === initial || (!adapter && text === clean(readProjection ??= project(read))) ? undefined : text;
}
function save() {
  clearTimeout(saveTimer);
  // Over the limit nothing goes out: the board keeps saying „Neuloženo“ until the text is shortened.
  if (done || !holding || !props.binding.save || measure()) return;
  const { send } = outgoing(changedText() ?? initial, true);
  if (send !== undefined) { props.binding.save(send); own.add(base = send); }
  saved = base;
  hold(false);
}
// A reload or a closed tab unmounts nothing: what was typed goes out as the page is hidden, or, for a binding that saves only
// at the end (a contact field), as it is left.
function hidden() { if (document.visibilityState === "hidden") save(); }
function left() { if (props.binding.save) save(); else finish(true); }

/** The text the read-only editor shows (the binding's, as it was last read into it). */
let shown = initial;
/** An editing starts: from the binding's text now, or from the draft an editing taken away kept. */
function start() {
  initial = adapter ? "" : props.binding.read();
  kept = !adapter && props.draftKey ? drafts.get(props.draftKey) as string | undefined : undefined;
  // At rest the editor already holds the text as read: nothing to read again, and its projection is the read text's (cheap).
  const holding = kept === undefined && shown === initial && !!controller.value;
  readProjection = holding ? controller.value!.getProjection().markdown : undefined;
  if (!holding) read = boardTextDocument(initial);
  if (kept !== undefined || shown !== initial) draft.value = kept === undefined ? read : boardTextDocument(kept);
  shown = initial;
  edited = kept !== undefined;
  done = false;
  saved = undefined;
  base = initial;
  own = new Set([initial]);
  conflict.value = false;
  over.value = 0;
  if (kept !== undefined) measure();
}
// At rest the editor follows the binding's text in place (a colleague's change, this person's own commit coming back).
watch(() => (adapter || props.active ? undefined : props.binding.read()), text => {
  if (text === undefined || text === shown) return;
  shown = text;
  draft.value = boardTextDocument(text);
});

function finish(force = false) {
  if (done) return;
  // Without the right to write any more (a lock, a lost right): what was typed waits as the draft for the next editing.
  if (force && props.host.canWrite?.() === false && !adapter && props.draftKey && edited) {
    keep(props.draftKey, clean(current(), Infinity));
    done = true;
    hold(false);
    emit("cancel");
    return;
  }
  if (!force && measure()) { controller.value?.focus(); return; }
  clearTimeout(saveTimer);
  clearTimeout(measureTimer);
  // Taken away over the limit: the full text is kept as a draft, never saved cut to the limit.
  if (force && measure()) {
    if (!adapter && props.draftKey) keep(props.draftKey, clean(current(), Infinity));
    done = true;
    hold(false);
    emit("cancel");
    return;
  }
  const text = changedText();
  // Saved while editing: only what changed since goes to the binding; never saved: only an edit.
  const out = saved !== undefined || text !== undefined ? outgoing(text ?? initial, !force) : undefined;
  if (out?.conflict && !force && props.binding.save) {
    // Both versions of one place: they are saved and stay in the editor, said above it, until the editing ends again.
    if (out.send !== undefined) { props.binding.save(out.send); own.add(base = saved = out.send); }
    hold(false);
    controller.value?.focus();
    return;
  }
  done = true;
  if (props.draftKey) keep(props.draftKey, undefined);
  hold(false);
  if (out?.send !== undefined) props.binding.commit(out.send);
  if (out && (saved !== undefined || out.send !== undefined)) emit("commit", out.send ?? base);
  else emit("cancel");
}

const pmElement = () => root.value?.querySelector<HTMLElement>(".ProseMirror") ?? null;
/** A popup of this editor is open (slash menu, link field): Enter and Esc belong to it. Decorations (the block frame) do not count. */
const overlayOpen = () => markdownOverlaysFor(pmElement())
  .some(overlay => !overlay.hidden && overlay.dataset.mdOverlayConsumesEscape !== "false" && overlay.getAttribute("aria-hidden") !== "true");
// ProseMirror prevents every Esc (by its key code), so whether a popup takes this one is read on its way in, before the popup closes.
let escapeForPopup = false;
function keydownCapture(event: KeyboardEvent) {
  if (done) return;
  if (event.key === "Escape") { escapeForPopup = event.isComposing || overlayOpen(); return; }
  if (!props.inline || event.key !== "Enter" || event.shiftKey || event.isComposing || overlayOpen()) return;
  event.preventDefault();
  event.stopPropagation();
  finish();
}
function keydown(event: KeyboardEvent) {
  if (done || event.key !== "Escape" || escapeForPopup) return;
  // The canvas must not also read it as „clear the selection“.
  event.preventDefault();
  event.stopPropagation();
  finish();
}
function outside(event: PointerEvent) {
  const target = event.target as Node | null;
  if (!target || done || root.value?.contains(target)) return;
  if (markdownOverlaysFor(pmElement()).some(overlay => overlay.contains(target))) return;
  finish();
}
function listen(on: boolean) {
  if (on) {
    document.addEventListener("pointerdown", outside, true);
    window.addEventListener("pagehide", left);
    document.addEventListener("visibilitychange", hidden);
  } else {
    document.removeEventListener("pointerdown", outside, true);
    window.removeEventListener("pagehide", left);
    document.removeEventListener("visibilitychange", hidden);
  }
}
// Only an editing listens for its end; the read-only editor at rest has no listener of its own (3.6-0 rule 4).
watch(() => props.active, (now, before) => {
  if (now) {
    start();
    listen(true);
    // Once the editor is writable (the prop reaches it on the next render): the caret where the person aimed.
    if (before === false) void nextTick(() => focusEnd());
    return;
  }
  if (before === undefined) return;
  listen(false);
  // Taken away (the canvas ended it: a click elsewhere, the item went, the right went): as if the editor went, then read-only again.
  finish(true);
  over.value = 0;
  conflict.value = false;
  // A kept draft is not the item's text: the editor shows the stored one again. Otherwise it keeps what it shows until the echo.
  if (props.draftKey && drafts.get(props.draftKey) !== undefined) { shown = props.binding.read(); draft.value = boardTextDocument(shown); }
}, { immediate: true });
// A double-click is a click first: the click into the selected card started the editing, its second press selects the word.
watch(() => props.at, at => { if (props.active && at?.word) void nextTick(() => focusEnd()); });
onBeforeUnmount(() => {
  clearEditorUndo(undoToken);
  if (props.active) listen(false);
  unsubscribe?.();
  // Taken away mid-edit (the item went, the board closed): what was typed still reaches the binding, cut to the limit if it must be.
  finish(true);
});
defineExpose({ finish: () => finish() });
</script>

<template>
  <div ref="root" class="board-text-editor" :class="{ 'is-editing nodrag nopan nowheel': active }" data-board-text-editor @keydown.capture="keydownCapture" @keydown="keydown">
    <!-- @vue-generic {'rich'} --><MarkdownEditor v-if="adapter" profile="rich" :collaboration="adapter" :ariaLabel="ariaLabel" :plugins="plugins" :lexicon="lexicon"
      :placeholder="placeholder" :readonly="!active" :outline="editorOutline" v-bind="editorChrome" :fullscreenable="false" bubble-on-body :formatting="formatting"
      :class="[theme, 'atlas-document-editor', 'board-text', { 'is-inline': inline }]" @created="created" @change="changed" />
    <!-- @vue-generic {'rich'} --><MarkdownEditor v-else v-model="draft" profile="rich" :ariaLabel="ariaLabel" :plugins="plugins" :lexicon="lexicon"
      :placeholder="placeholder" :readonly="!active" :outline="editorOutline" v-bind="editorChrome" :fullscreenable="false" bubble-on-body :formatting="formatting"
      :class="[theme, 'atlas-document-editor', 'board-text', { 'is-inline': inline }]" @created="created" @change="changed" />
    <p v-if="over" class="board-text-limit" role="alert" data-board-text-limit>{{ overMessage }}</p>
    <p v-if="conflict" class="board-text-conflict" role="status" data-board-text-conflict>{{ t.textEditor.conflict }}</p>
  </div>
</template>

<style scoped>
.board-text-editor { display: flex; flex-wrap: wrap; width: 100%; min-width: 0; }
.board-text-editor.is-editing { cursor: text; }
/* A reader's text (no card to drag): its words are selected and copied as on a page (contract J2.5). */
.board-text-editor.is-readable :deep(.ProseMirror) { cursor: text; user-select: text; -webkit-user-select: text; }
.board-text-limit { flex-basis: 100%; margin: 4px 0 0; color: var(--error); font-size: 12.5px; animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
.board-text-conflict { flex-basis: 100%; order: -1; margin: 0 0 4px; color: var(--warning); font-size: 12.5px; animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
</style>
