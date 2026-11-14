<script setup lang="ts">
import t from "../../../i18n/boards";
import { computed, nextTick, onBeforeUnmount, onErrorCaptured, onMounted, ref, shallowRef, watch } from "vue";
import { MarkdownEditor } from "@atlas/markdown/vue";
import { markdownOverlaysFor, markdownStrings, type MarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from "@atlas/markdown/core";
import { Extension, type Editor } from "@tiptap/core";
import { Plugin } from "@tiptap/pm/state";
import "@atlas/markdown/styles/base.css";
import "@atlas/markdown/themes/ocean.css";
import "@atlas/markdown/themes/forest.css";
import "@atlas/markdown/themes/midnight.css";
import type { BoardBlockNode } from "../../../boards/boardApi";
import {
  asBlockNode, asStoredNode, blockDocument, blocksOf, boardTextPlugins, firstBlock, inertBlockHost, isKnownNode, type BoardBlockHost, type EditorBlockModule,
} from "../../../boards/editorBlocks";
import { emptyDocument } from "../../../document";
import { editorChrome, editorOutline, useEditorTheme } from "../../../editor/editorSetup";
import { placeCaret, type EditPoint } from "../../../boards/boardCaret";
import { restingSpellcheck } from "../text/boardMarkdownText";
import { motionMs } from "../../../motion/aiMotion";
import { blockName } from "../../../boards/boardFlow";
import { kindOfNode } from "../../../overview/overviewKinds";
import OverviewReadCard from "../../../overview/OverviewReadCard.vue";
import AtlasIcon from "../../AtlasIcon.vue";
import "../text/boardTextSurface.css";
import "./boardBlockLayouts.css";

/**
 * One editor block on a board (a `block` item, spec §3.6): a contact, chart, diagram, formula, YouTrack issue, callout, table…
 * rendered by the page editor's own plugin, so the board has no component of its own for any of them.
 *
 * - One editor for reading and writing: a small page editor holding only this block
 *   draws it, read-only at rest and for a reader, once the block is close enough to be read and in view (a light preview tile of
 *   its size until then). Links and controls inside it take the pointer from the canvas (no drag); its text does not, so the block
 *   is selected and dragged by it.
 * - `editing` (the canvas sets it on a double-click or Enter) makes that same editor writable, with the plugin's own editing:
 *   its context toolbar is opened on the block and its form or panel with it („Upravit: …“); a callout or a table is edited in
 *   place. Esc or a click outside ends it: `commit(node)` with the changed node, `cancel` when nothing changed, `remove` when the
 *   person deleted the block in the editor.
 * - `insert` starts the same editor empty and runs the module's own insertion (`useEditorBlockModules`): the plugin's
 *   defaults with its form, or its picker (YouTrack). Nothing inserted ends as `cancel`.
 * - A node of a plugin this Atlas does not know renders as a fallback card that keeps the stored data untouched and cannot be
 *   edited here.
 * - An overview (`overview`) is its kind's card over the whole board at rest (OverviewReadCard with `host.board`), never edited.
 * - A requirement's controls work at rest (`ownControls`): the check, the state chip and the panel of its own view
 *   (`requirementBlockView.ts`) change it without editing the block; a change of its state or facts is committed at once, with the
 *   view's own motion. Its text takes no pointer and no Tab stop at rest, so the block is still selected and dragged by it and Tab
 *   goes item → check → chip → pencil. `readonly` (a reader) keeps the view and disables its controls.
 * - The board stores the whole node, so a commit carries only what this person changed since the editor took the block
 *   (`mergeChange`) over what the board holds now, and a collaborator's change of an attribute reaches the open editor.
 *
 * `host` carries the plugins' host services in the board's context (`boardContextBlockHost(boardId)`); create it once per board,
 * every block of the board shares its plugins. An inline node (a YouTrack issue, as the pickers store it) renders in its
 * paragraph and is stored back as the bare node (`asBlockNode`, `asStoredNode`).
 */
const props = withDefaults(defineProps<{
  block: BoardBlockNode | null;
  editing?: boolean;
  insert?: EditorBlockModule;
  host?: BoardBlockHost;
  ariaLabel?: string;
  readonly?: boolean;
  /** The block fills its item's box (BoardNode, `fill`): a formula is scaled to it. */
  fit?: boolean;
  /** The item's colour: at rest the block is drawn again when it changes (a chart reads its text colours when it draws). */
  tone?: string;
  /** The board is zoomed out too far to read it (BoardCanvas `overview`): until it has been drawn once, a light preview stands in. */
  overview?: boolean;
  /** The item's height, the preview's while the block that flows has no content of its own drawn yet. */
  previewHeight?: number;
  /** Where the editing was aimed (a double-click, a click into the selected block); the end of its text without it. */
  at?: EditPoint;
}>(), { editing: false, host: () => inertBlockHost, ariaLabel: t.block.label, readonly: false, fit: false });
/** `save`: the block so far while it is edited (the editing goes on); `unsaved`: whether the editor holds a change not yet saved. */
const emit = defineEmits<{ commit: [node: BoardBlockNode, others?: BoardBlockNode[]]; save: [node: BoardBlockNode]; cancel: []; remove: []; unsaved: [holding: boolean] }>();

const theme = useEditorTheme();
const known = computed(() => !!props.block && isKnownNode(props.block));
const failed = ref(false);
watch(() => props.block, () => { failed.value = false; });
// A block whose stored data the plugin refuses renders as the fallback instead of breaking the board; an editor that fails
// ends the editing and leaves the block as it was.
onErrorCaptured(() => {
  // What the failed editor held is gone with it: no late save, and the board stops saying „Neuloženo“ (W4-6).
  clearTimeout(saveTimer);
  hold(false);
  if (!editable.value) failed.value = true;
  else if (!done) { done = true; emit("cancel"); }
  return false;
});
const renderable = computed(() => !!props.block && known.value && !failed.value);
const value = computed(() => (props.block ? blockDocument(asBlockNode(props.block)) : emptyDocument()));
const fallbackText = computed(() => textOf(props.block).trim().slice(0, 280));
function textOf(node: unknown): string {
  if (!node || typeof node !== "object") return "";
  const { text, content } = node as { text?: unknown; content?: unknown };
  return (typeof text === "string" ? text : "") + (Array.isArray(content) ? content.map(textOf).join(" ") : "");
}

// An overview has nothing to edit: its card works at rest (its level, its actions); the canvas moves and deletes it as any block.
const overviewKind = computed(() => kindOfNode(props.block?.type, props.block?.attrs));
const editable = computed(() => props.editing && (!!props.insert || (renderable.value && overviewKind.value === undefined)));

// The block's editor exists at rest (read-only) once it has been drawn; a requirement's (its controls work at rest) from the start.
const ownControls = computed(() => props.block?.type === "requirement");
const live = computed(() => !props.insert && renderable.value && overviewKind.value === undefined && (ownControls.value || drawn.value));
// The canvas asked to edit a block that cannot be: nothing changes.
watch(() => props.editing && !editable.value, refused => { if (refused) emit("cancel"); }, { immediate: true });

const startValue = () => (props.insert ? emptyDocument() : value.value);
const draft = shallowRef<MarkdownRichValue>(startValue());
// The block as the draft took it: what differs from it in the draft is this person's change.
let base = props.block;
const root = ref<HTMLElement>();
// A formula that fills its item takes the size the box allows: its font grows or shrinks until it just fits (owner, 28. 10.).
let fitObserver: ResizeObserver | undefined;
function fitFormula() {
  const box = root.value, formula = box?.querySelector<HTMLElement>(".katex");
  if (!box || !formula) return;
  box.removeAttribute("data-fit-pending");
  const size = parseFloat(getComputedStyle(formula).fontSize) || 16;
  const scale = Math.min(box.clientWidth / (formula.scrollWidth || 1), box.clientHeight / (formula.scrollHeight || 1)) * 0.9;
  formula.style.fontSize = `${Math.min(160, Math.max(10, size * scale))}px`;
}
// A chart and a diagram read their colours when they draw: once a new colour has faded in (the node's --atlas-motion-slow), they
// draw again in it. Other components follow the colour by their tokens and are never drawn anew.
const drawnTone = ref(props.tone ?? "");
// Drawn once, it stays drawn: zooming out again costs nothing; a block never drawn waits until the board is close enough and the
// block is in view, so a fit view after a close look draws only what that look had shown.
const drawn = ref(false), inView = ref(typeof IntersectionObserver === "undefined");
// The tile has been on screen (a frame has passed since mounting): a block drawn at once does not fade.
let shown = false;
onMounted(() => requestAnimationFrame(() => { shown = true; }));
let viewObserver: IntersectionObserver | undefined;
watch(root, box => {
  viewObserver?.disconnect();
  if (!box || drawn.value || typeof IntersectionObserver === "undefined") return;
  // Added to a board read close up (not at its opening, where every node mounts at once): in view now, not a frame later.
  if (!props.overview) {
    const r = box.getBoundingClientRect();
    inView.value = r.width > 0 && r.bottom > -200 && r.top < innerHeight + 200 && r.right > -200 && r.left < innerWidth + 200;
  }
  viewObserver = new IntersectionObserver(([entry]) => { inView.value = !!entry?.isIntersecting; }, { rootMargin: "200px" });
  viewObserver.observe(box);
});
// Drawn in place of a preview tile, it fades in as content taking a skeleton's place does (LoadingRegion `data-arrived`).
const arrived = ref(false);
watch(() => !props.overview && inView.value, now => {
  if (!now) return;
  arrived.value = shown;
  drawn.value = true;
  viewObserver?.disconnect();
}, { immediate: true });
onBeforeUnmount(() => viewObserver?.disconnect());
const previewName = computed(() => blockName(props.block?.type));
let toneTimer: ReturnType<typeof setTimeout> | undefined;
// Never while it is edited: drawing anew is a new editor, which would end the editing; the colour follows once it ends.
watch(() => [props.tone ?? "", editable.value] as const, ([tone, editing]) => {
  clearTimeout(toneTimer);
  if (editing || tone === drawnTone.value || (props.block?.type !== "chart" && props.block?.type !== "diagram")) return;
  toneTimer = setTimeout(() => { drawnTone.value = tone; }, motionMs(root.value ?? document.documentElement, "--atlas-motion-slow") + 40);
});
onBeforeUnmount(() => clearTimeout(toneTimer));
let formulaObserver: MutationObserver | undefined;
watch(() => props.fit && !editable.value && props.block?.type === "mathBlock" && root.value, box => {
  fitObserver?.disconnect();
  formulaObserver?.disconnect();
  fitObserver = formulaObserver = undefined;
  // Editing (or no formula): nothing waits for a fit, and nothing stays hidden.
  if (!box || typeof ResizeObserver === "undefined") { root.value?.removeAttribute("data-fit-pending"); return; }
  fitObserver = new ResizeObserver(() => requestAnimationFrame(fitFormula));
  fitObserver.observe(box);
  box.setAttribute("data-fit-pending", "");
  // The formula's renderer loads on demand and draws again on a change: each new drawing is fitted when it arrives.
  formulaObserver = new MutationObserver(records => {
    if (!records.some(record => record.type === "childList")) return;
    box.setAttribute("data-fit-pending", "");
    requestAnimationFrame(fitFormula);
  });
  formulaObserver.observe(box, { childList: true, subtree: true });
}, { flush: "post" });
onBeforeUnmount(() => { fitObserver?.disconnect(); formulaObserver?.disconnect(); });
let editor: Editor | undefined;
let controller: MarkdownEditorController<"rich"> | undefined;
let done = false;
let begun = false;
let opening: ReturnType<typeof setTimeout> | undefined;
watch(editable, open => {
  if (!open) return;
  done = begun = false;
  // A live block keeps its editor: editing begins in it once it is writable.
  if (live.value) { base = props.block; void nextTick(begin); return; }
  base = props.block;
  draft.value = startValue();
  controller = undefined;
});
// At rest a live block shows what the board holds, and commits what its controls changed (the state, the facts) at once.
// While it is edited, a collaborator's change of an attribute goes into the editor and stops counting as this person's.
watch(() => props.block, (now, before) => {
  if (live.value && !editable.value) { base = now; draft.value = value.value; return; }
  if (!editable.value || !editor || !now || !before || !base) return;
  const changed = changedAttrs(before, now);
  if (!changed.length) return;
  base = { ...base, attrs: { ...base.attrs, ...Object.fromEntries(changed.map(key => [key, now.attrs?.[key]])) } };
  let at = -1;
  editor.state.doc.descendants((node, position) => { if (at < 0 && node.type.name === now.type) at = position; return at < 0; });
  if (at < 0) return;
  // This person's own save coming back changes nothing in the editor.
  const node = editor.state.doc.nodeAt(at);
  const differing = changed.filter(key => JSON.stringify(node?.attrs[key] ?? null) !== JSON.stringify(now.attrs?.[key] ?? null));
  if (!differing.length) return;
  const tr = editor.state.tr.setMeta("addToHistory", false);
  for (const key of differing) tr.setNodeAttribute(at, key, now.attrs?.[key] ?? null);
  editor.view.dispatch(tr);
});
// While edited, the block reaches the board after a pause in typing and when the page is hidden (a reload, a closed tab), and
// the editing goes on (review cycle 3, U3-1); until then the board says „Neuloženo“.
const savePause = 1000;
let saveTimer: ReturnType<typeof setTimeout> | undefined;
let holding = false;
function hold(now: boolean) {
  if (holding !== now) emit("unsaved", holding = now);
}
// An edited item keeps only its first block: the root's `data-md-single-block` has a plugin add to that block, not after it.
/** The edited block as the board would store it, when it differs from what the board holds; none for a deleted block. */
function changedNode() {
  const result = firstBlock(draft.value.document);
  const stored = result && mergeChange(asStoredNode(result));
  return stored && JSON.stringify(stored) !== JSON.stringify(props.block) ? stored : undefined;
}
function save() {
  clearTimeout(saveTimer);
  if (done || !editable.value || props.insert || !props.block) return;
  const node = changedNode();
  if (node) { base = node; emit("save", node); }
  hold(false);
}
function hidden() { if (document.visibilityState === "hidden") save(); }
watch(draft, () => {
  if (!editable.value || done || props.insert || !props.block) return;
  clearTimeout(saveTimer);
  if (!changedNode()) { hold(false); return; }
  hold(true);
  saveTimer = setTimeout(save, savePause);
});
// A module inserting by its own panel is done once the panel inserted.
watch(draft, now => { if (props.insert?.open && editable.value && firstBlock(now.document)) finish(); });
watch(draft, now => {
  if (!live.value || !ownControls.value || editable.value || !props.block) return;
  const result = firstBlock(now.document);
  const merged = result && mergeChange(asStoredNode(result));
  if (merged && JSON.stringify(merged.attrs) !== JSON.stringify(props.block.attrs)) emit("commit", merged);
});
function changedAttrs(from: BoardBlockNode, to: BoardBlockNode) {
  const keys = new Set([...Object.keys(from.attrs ?? {}), ...Object.keys(to.attrs ?? {})]);
  return [...keys].filter(key => JSON.stringify(from.attrs?.[key]) !== JSON.stringify(to.attrs?.[key]));
}
/** This person's change (the edited node against `base`) over the block the board holds now. */
function mergeChange(edited: BoardBlockNode): BoardBlockNode {
  const current = props.block;
  if (!current || !base || current.type !== edited.type || base.type !== edited.type) return edited;
  const attrs = { ...current.attrs };
  for (const key of changedAttrs(base, edited)) {
    if (edited.attrs && key in edited.attrs) attrs[key] = edited.attrs[key];
    else delete attrs[key];
  }
  const ownContent = JSON.stringify(base.content) !== JSON.stringify(edited.content);
  const content = ownContent ? edited.content : current.content;
  return { type: edited.type, ...(Object.keys(attrs).length || edited.attrs ? { attrs } : {}), ...(content ? { content } : {}) };
}
/**
 * Tab reaches the controls of a live block, not its text: the text is edited as the block is (Enter, a double-click). Focusing
 * the text would also end in the requirement's key on leaving it, a board change for every Tab (review cycle 2, W2-1).
 */
const atRest = () => live.value && !editable.value;
const restTabStop = Extension.create({
  name: "atlasBoardRestTabStop",
  priority: 1000, // before the editor's own `tabindex` (the first attribute wins)
  addProseMirrorPlugins: () => [new Plugin({ props: { attributes: (): Record<string, string> => (atRest() ? { tabindex: "-1" } : {}) } })],
});
watch(atRest, () => { if (editor && !editor.isDestroyed) editor.view.updateState(editor.state); });
const resting = restingSpellcheck(() => editable.value);
// The mini editor's ProseMirror editor, for what the controller's commands cannot do: select the component.
const startEditing: MarkdownPlugin = {
  id: "atlas-board-block-editing", apiVersion: 1,
  create: () => ({
    extensions: [restTabStop],
    onAttach: ({ editor: attached }) => {
      editor = attached;
      queueMicrotask(begin);
      return () => { if (editor === attached) editor = undefined; };
    },
  }),
};
const editPlugins = computed(() => [...boardTextPlugins(props.host), startEditing, resting]);
function created(instance: MarkdownEditorController<"rich">) {
  controller = instance;
  begin();
}

/**
 * Editing starts on the block, once the editor has both its view and its controller: an insertion runs the module's command;
 * a component is selected (its context toolbar) and its form opened; anything else (a callout, a table) gets the caret.
 */
function begin() {
  if (begun || !editor || !controller || !editable.value) return;
  begun = true;
  controller.focus();
  if (props.insert) {
    if (props.insert.open) props.insert.open(editor);
    else if (props.insert.command) controller.dispatch(props.insert.command);
    return;
  }
  let atom: number | undefined;
  editor.state.doc.descendants((node, position) => {
    if (atom === undefined && node.isAtom && !node.isText) atom = position;
    return atom === undefined;
  });
  if (atom === undefined) { placeCaret(editor, props.at, { block: true }); return; }
  editor.chain().setNodeSelection(atom).focus().run();
  openForm(40);
}
/**
 * Opens the plugin's form once its context toolbar shows its edit button („Upravit: …“, the editor's own label): the plugin
 * loads its runtime first. shortcut: polls the editor's overlays for up to two seconds instead of an API to open a module's
 * editing, which the editor does not offer yet; switch to one when it does. A component without a form keeps its toolbar.
 */
function openForm(tries: number) {
  clearTimeout(opening);
  if (!editor || tries <= 0 || done) return;
  const prefix = markdownStrings.modules.editBlockLabel("");
  const edit = markdownOverlaysFor(editor.view.dom)
    .filter(overlay => overlay.classList.contains("md-context-toolbar") && !overlay.hidden)
    .flatMap(toolbar => [...toolbar.querySelectorAll<HTMLButtonElement>("button:not([disabled])")])
    .find(button => button.getAttribute("aria-label")?.startsWith(prefix));
  if (edit) edit.click();
  else opening = setTimeout(() => openForm(tries - 1), 50);
}

function finish() {
  if (done || !editable.value) return;
  done = true;
  clearTimeout(opening);
  clearTimeout(saveTimer);
  hold(false);
  const result = firstBlock(draft.value.document);
  if (!result) { if (props.insert || !props.block) emit("cancel"); else emit("remove"); return; }
  if (props.insert) {
    // An insertion may bring more than one block (a catalog requirement and what it requires): each becomes a block of the board.
    const [first, ...others] = blocksOf(draft.value.document).map(asStoredNode);
    emit("commit", first!, others);
    return;
  }
  const stored = mergeChange(asStoredNode(result));
  if (props.block && JSON.stringify(stored) === JSON.stringify(props.block)) { emit("cancel"); return; }
  emit("commit", stored);
}
const ownsTarget = (target: Node) => root.value?.contains(target)
  || (editor && markdownOverlaysFor(editor.view.dom).some(overlay => overlay.contains(target)));
// ProseMirror prevents every Esc (by its key code), so whether a popup or form of the plugin takes this one is read on its way in.
let escapeForPopup = false;
function keydownCapture(event: KeyboardEvent) {
  if (event.key !== "Escape") return;
  escapeForPopup = event.isComposing || (!!editor && markdownOverlaysFor(editor.view.dom)
    .some(overlay => !overlay.hidden && overlay.dataset.mdOverlayConsumesEscape !== "false" && overlay.getAttribute("aria-hidden") !== "true"));
}
function keydown(event: KeyboardEvent) {
  if (event.key !== "Escape" || escapeForPopup || !editable.value) return;
  event.preventDefault();
  event.stopPropagation();
  finish();
}
function outside(event: PointerEvent) {
  const target = event.target as Node | null;
  if (target && editable.value && !ownsTarget(target)) finish();
}
function listen(open: boolean) {
  if (open) {
    document.addEventListener("pointerdown", outside, true);
    window.addEventListener("pagehide", save);
    document.addEventListener("visibilitychange", hidden);
  } else {
    document.removeEventListener("pointerdown", outside, true);
    window.removeEventListener("pagehide", save);
    document.removeEventListener("visibilitychange", hidden);
  }
}
watch(editable, listen, { immediate: true });
onBeforeUnmount(() => {
  clearTimeout(opening);
  listen(false);
  // Taken away mid-edit (the canvas stopped drawing the node, the board closed): the change still reaches the canvas.
  finish();
});

/** A press on a control or link inside the rendered block belongs to it, not to the canvas (no drag, no selection change). */
function keepControl(event: Event) {
  if ((event.target as Element | null)?.closest?.("a, button, input, select, textarea, summary, label, [role=button], [role=slider], [data-slot=slider], [contenteditable=true]")) event.stopPropagation();
}
defineExpose({ finish });
</script>

<template>
  <div ref="root" class="board-block" :class="{ 'is-editing nodrag nopan nowheel': editable, 'is-live': live && !editable, 'is-readable nopan': readonly && !editable }" data-board-block :data-md-single-block="!insert || undefined"
    :data-block-type="block?.type" @keydown.capture="keydownCapture" @keydown="keydown" @pointerdown="keepControl" @mousedown="keepControl" @touchstart.passive="keepControl">
    <!-- @vue-generic {'rich'} -->
    <MarkdownEditor v-if="editable || live" :key="drawnTone" v-model="draft" profile="rich" :ariaLabel="ariaLabel" :plugins="editPlugins" :outline="editorOutline"
      :readonly="!editable && (readonly || !ownControls)" v-bind="editorChrome" :show-block-add-button="false" :fullscreenable="false" :data-arrived="arrived || undefined"
      :class="[theme, 'atlas-document-editor', 'board-text']" @created="created" />
    <!-- @vue-generic {'rich'} -->
    <!-- Hidden from a reader: the item itself carries the component's name. -->
    <div v-else-if="renderable && !drawn" class="board-block-preview" aria-hidden="true" :style="{ '--board-block-preview-height': previewHeight ? `${previewHeight}px` : undefined }"
      data-board-block-preview>{{ previewName }}</div>
    <!-- An overview over the whole board (its items, host.board), not over this one block. -->
    <OverviewReadCard v-else-if="renderable && overviewKind !== undefined && host.board" :kind="overviewKind" :scope="host.board.id" :board="host.board" data-board-overview />
    <div v-else-if="block" class="board-block-fallback" role="group" :aria-label="t.block.unknown" data-board-block-fallback>
      <AtlasIcon name="box" :size="18" />
      <div class="board-block-fallback-text">
        <strong>{{ t.block.unsupported(block.type) }}</strong>
        <p v-if="fallbackText">{{ fallbackText }}</p>
        <small>{{ t.block.dataKept }}</small>
      </div>
    </div>
  </div>
</template>

<style scoped>
.board-block { display: flex; width: 100%; min-width: 0; min-height: 0; }
.board-block.is-editing { cursor: auto; }
/* A block at rest: its text belongs to the board (select, drag), its links and controls to the block. */
.board-block.is-live:not(.is-readable) :deep(.ProseMirror) { pointer-events: none; }
/* An item is its first block (firstBlock): what the editor keeps after it (its trailing paragraph, once writable) is no part of the item, and
   the card keeps its size. No :has(): flipping the editor's contenteditable would invalidate it over the whole board. */
.board-block[data-md-single-block] :deep(.ProseMirror > :not(:first-child)) { display: none; }
.board-block.is-readable :deep(.ProseMirror) { cursor: text; user-select: text; -webkit-user-select: text; }
.board-block.is-live :deep(:is(a, button, input, select, summary, label, [role=button], [role=slider], [data-slot=slider])) { pointer-events: auto; }
/* At rest the board moves the block: the editor's block handle is not drawn and is no Tab stop (W2-1). */
.board-block.is-live :deep(.dh-wrap) { display: none; }
/* A block too small to read and not drawn yet: its name on a quiet tile in the component's own colours, sized like the item. */
.board-block > [data-arrived] { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.board-block-preview { display: grid; place-items: center; width: 100%; height: 100%; min-height: var(--board-block-preview-height, 48px); padding: 8px;
  border: 1px dashed color-mix(in oklab, var(--board-block-fg, var(--muted)) 30%, transparent); border-radius: var(--atlas-radius-md);
  color: var(--board-block-fg, var(--muted)); font-size: 14px; font-weight: 600; text-align: center; overflow-wrap: anywhere; }
.board-block-fallback { display: flex; gap: 10px; align-items: flex-start; width: 100%; padding: 12px; border: 1px dashed var(--line); border-radius: 10px; background: var(--surface); color: var(--muted); font-size: 13px; }
.board-block-fallback-text { display: grid; gap: 4px; min-width: 0; }
.board-block-fallback strong { color: var(--text); font-weight: 600; overflow-wrap: anywhere; }
.board-block-fallback p { margin: 0; overflow: hidden; color: var(--text); text-overflow: ellipsis; overflow-wrap: anywhere; }
</style>
