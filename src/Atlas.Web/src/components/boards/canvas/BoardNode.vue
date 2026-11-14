<script lang="ts">
import { lazyComponent } from "../../../lazyComponent";

// Not in a graph that only reads: its nodes are page cards, so the text renderer, the editor blocks and their plugins stay out of it.
// Defined once for every node, not in a node's setup: a chunk's first load keeps its loader (its stylesheet's listeners), which kept
// the node, the canvas and the closed board in memory.
const PagePreview = lazyComponent(() => import("../preview/PagePreview.vue"));
const DeploymentBoardCard = lazyComponent(() => import("../../../projector/DeploymentBoardCard.vue"));
const ObjectCard = lazyComponent(() => import("../cards/ObjectCard.vue"));
const BoardBlock = lazyComponent(() => import("../blocks/BoardBlock.vue"));
const BoardLinks = lazyComponent(() => import("../cards/BoardLinks.vue"));
</script>

<script setup lang="ts">
import t from "../../../i18n/boards";
import { Handle, Position } from "@vue-flow/core";
import { NodeResizeControl, NodeResizer, ResizeControlVariant, type OnResize } from "@vue-flow/node-resizer";
import { computed, inject, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { blockLayoutOf, minSizeOf, propsOf, type BoardNodeData } from "../../../boards/boardFlow";
import { fontSizes, noteFontSize } from "../../../boards/boardText";
import PageCard from "../cards/PageCard.vue";
import BoardText from "../text/BoardText.vue";
import type { TextBinding } from "../text/textBinding";
import CollaboratorAvatars from "../../CollaboratorAvatars.vue";
import AtlasIcon from "../../AtlasIcon.vue";
import BoardBlockFallback from "./BoardBlockFallback.vue";
import { boardCanvasKey } from "./boardCanvasContext";
import { colorAttributes } from "./boardPalette";
import { playNodeMotion, type PlayedMotion } from "./boardAnimations";

/**
 * One board item as a Vue Flow node: its body by kind (note, text, shape, frame; a page card and an editor block from their own
 * components, an object card from its type's view), four connection handles that show on hover, eight resize handles while it is
 * the only selected item (a note keeps its aspect ratio unless Shift is held) and the text edited in place. Everything it changes goes to the canvas (context), which
 * turns it into one batch. Vue Flow passes more props than these; they are not attributes of the element.
 */
defineOptions({ inheritAttrs: false });
const props = defineProps<{ id: string; data: BoardNodeData & { motion?: PlayedMotion }; selected?: boolean; dragging?: boolean; position?: { x: number; y: number } }>();
const ctx = inject(boardCanvasKey)!;
const root = ref<HTMLElement>();

const item = computed(() => props.data.item);
const kind = computed(() => item.value.kind);
const options = computed(() => propsOf(item.value));
const links = computed(() => ctx.blockHost?.links);
const editing = computed(() => ctx.per.editing(props.id));
const sides = [Position.Top, Position.Right, Position.Bottom, Position.Left];
/** Its text is a BoardText, whose editor places the caret and takes the focus itself (BoardTextEditor `focusEnd`). */
const textual = computed(() => kind.value === "note" || kind.value === "text" || kind.value === "shape" || kind.value === "frame" || kind.value === "group");
// A group's box follows its members (boardGroups.ts), so it has no resize handles.
const single = computed(() => ctx.per.single(props.id));
const resizable = computed(() => ctx.canEdit.value && single.value && !editing.value && props.data.motion?.kind !== "leave" && kind.value !== "group");
const collapsed = computed(() => kind.value === "group" && options.value.collapsed === true);
/** An editor component without a card: it fills its box or flows in its width (boardFlow.blockLayoutOf). */
const layout = computed(() => blockLayoutOf(item.value));
const min = computed(() => minSizeOf(item.value));
/** Its own colour, a frame's neutral grey (the canvas decides). */
const tone = computed(() => ctx.toneOf(item.value));
const members = computed(() => {
  const count = props.data.members ?? 0;
  return t.node.members(count);
});
/** How the neighbour highlighting marks this node; what it leaves out dims by the canvas's mark (a hover redraws only the marked). */
const mark = computed(() => ctx.per.mark(props.id));
/** Where the gesture under way would land: the frame a drag drops into, the item a connector goes to (J1). */
const target = computed(() => ctx.per.dropTarget(props.id));
const setCollapsed = (value: boolean) => ctx.restyle(props.id, { props: { collapsed: value || null } });
const fontSize = computed(() => {
  const { width, height } = item.value;
  if (kind.value === "note") return noteFontSize(item.value.text ?? "", width, height, options.value.fontSize ?? "auto");
  const size = options.value.fontSize;
  return size && size !== "auto" ? fontSizes[size] : fontSizes.m;
});
const textStyle = computed(() => ({ fontSize: `${fontSize.value}px`, textAlign: options.value.align, fontWeight: options.value.bold ? 700 : undefined }));
const shapePath = computed(() => {
  switch (options.value.shape) {
    case "ellipse": return "ellipse";
    case "diamond": return "diamond";
    case "rounded": return "rounded";
    default: return "rect";
  }
});

// Every board text is Markdown (spec §3.4), drawn and edited by one page editor (BoardText: read-only at rest, an editing flips it in
// place). Its commit is one `set` of the text; Esc, a click outside and (for a title) Enter end it.
const binding: TextBinding = { read: () => item.value.text ?? "", commit: markdown => ctx.commitText(props.id, markdown), save: markdown => ctx.saveText(props.id, markdown) };
const editLabels: Record<string, string> = { note: t.node.noteText, text: t.node.text, shape: t.node.shapeText, frame: t.node.frameName, group: t.node.groupName };
/**
 * Vue Flow shows a new node only once it is measured; until then it is hidden and refuses the focus, so this tries a few frames. Not
 * for a text: its editor focuses itself, and focusing its stand-in until then only laid the whole board out each frame.
 */
function focusEditor(tries = 10) {
  const el = root.value?.querySelector<HTMLElement>(".ProseMirror");
  if (!editing.value || (el && root.value?.contains(document.activeElement))) return;
  el?.focus({ preventScroll: true });
  if (tries > 0) requestAnimationFrame(() => focusEditor(tries - 1));
}
watch(editing, now => { if (now && !textual.value) requestAnimationFrame(() => focusEditor()); }, { immediate: true, flush: "post" });

let lastResize: { x: number; y: number; width: number; height: number } | undefined;
function onResize(event: OnResize) {
  const { x, y, width, height } = event.params;
  lastResize = { x, y, width, height: layout.value === "flow" ? root.value?.offsetHeight || height : height };
  ctx.resizing(props.id, lastResize);
}
/** A component that flows keeps the height its content takes at the new width: read once that width is laid out. */
function onResizeEnd() {
  if (layout.value !== "flow" || !lastResize) { ctx.resized(props.id); return; }
  requestAnimationFrame(() => {
    if (lastResize && root.value) ctx.resizing(props.id, { ...lastResize, height: root.value.offsetHeight || lastResize.height });
    lastResize = undefined;
    ctx.resized(props.id);
  });
}

// A page's preview reads and draws the page only once its card comes near the screen with the board close enough to read it (as an
// editor block does, BoardBlock): a board's opening reads none of the previews it does not show. Read once, it stays drawn.
const preview = computed(() => kind.value === "page" && options.value.display === "preview");
const nearView = ref(typeof IntersectionObserver === "undefined");
let viewObserver: IntersectionObserver | undefined;
watch(() => (preview.value ? root.value : undefined), box => {
  viewObserver?.disconnect();
  if (!box || typeof IntersectionObserver === "undefined") return;
  viewObserver = new IntersectionObserver(([entry]) => { nearView.value = !!entry?.isIntersecting; }, { rootMargin: "200px" });
  viewObserver.observe(box);
});
onBeforeUnmount(() => viewObserver?.disconnect());

function play() {
  const motion = props.data.motion;
  if (motion && root.value) playNodeMotion(root.value, motion, { ...(props.position ?? item.value), width: item.value.width, height: item.value.height });
}
onMounted(play);
watch(() => props.data.motion?.key, play, { flush: "post" });
</script>

<template>
  <div ref="root" class="board-node" :data-kind="kind" :data-block-layout="layout" :data-motion="data.motion?.kind" :data-shape="kind === 'shape' ? shapePath : undefined" :data-highlight="mark"
    :data-collapsed="collapsed || undefined"
    :class="{ 'is-editing': editing, 'is-selected': selected, 'is-single': single, 'is-leaving': data.motion?.kind === 'leave', 'is-missing': kind === 'page' && item.target?.available === false, 'is-drop-target': target }"
    v-bind="colorAttributes(tone)">
    <template v-if="kind === 'note'">
      <div class="board-note" :style="textStyle">
        <BoardText :id="id" :editing="editing" :binding="binding" :text="item.text" :aria-label="editLabels.note" />
      </div>
    </template>
    <template v-else-if="kind === 'text'">
      <div class="board-free-text" :style="textStyle">
        <BoardText :id="id" :editing="editing" :binding="binding" :text="item.text" :placeholder="t.node.typeText" :aria-label="editLabels.text"><span v-if="ctx.canEdit.value" class="board-placeholder">{{ t.node.typeText }}</span></BoardText>
      </div>
    </template>
    <template v-else-if="kind === 'shape'">
      <svg class="board-shape" viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">
        <ellipse v-if="shapePath === 'ellipse'" cx="50" cy="50" rx="49" ry="49" vector-effect="non-scaling-stroke" />
        <polygon v-else-if="shapePath === 'diamond'" points="50,1 99,50 50,99 1,50" vector-effect="non-scaling-stroke" />
        <rect v-else x="1" y="1" width="98" height="98" :rx="shapePath === 'rounded' ? 12 : 1" vector-effect="non-scaling-stroke" />
      </svg>
      <div class="board-shape-text" :style="textStyle">
        <BoardText :id="id" :editing="editing" :binding="binding" :text="item.text" :aria-label="editLabels.shape" />
      </div>
    </template>
    <template v-else-if="kind === 'frame'">
      <div class="board-frame-title">
        <BoardText :id="id" :editing="editing" :binding="binding" :text="item.text" editor-class="board-frame-editor" text-class="board-frame-name" inline :placeholder="t.node.frameName" :aria-label="editLabels.frame"><span class="board-frame-name">{{ t.node.frame }}</span></BoardText>
      </div>
      <div class="board-frame-body" aria-hidden="true" />
      <!-- Its outline takes the pointer (a click selects, a drag moves it); the inside does only while it is selected. -->
      <div v-if="ctx.tool.value === 'select'" class="board-frame-edge" aria-hidden="true" />
    </template>
    <template v-else-if="kind === 'group'">
      <!-- Collapsed: one card standing for its members (their connectors merge onto it); open: a named outline around them. -->
      <div v-if="collapsed" class="board-group-card">
        <span class="board-group-card-name">
          <BoardText :id="id" :editing="editing" :binding="binding" :text="item.text" inline :placeholder="t.node.groupName" :aria-label="editLabels.group">{{ t.node.group }}</BoardText>
        </span>
        <span class="board-group-count" data-board-group-count>{{ members }}</span>
        <button v-if="ctx.canEdit.value" type="button" class="board-group-toggle nodrag" :aria-label="t.node.expand" :title="t.node.expand" data-board-group-expand
          @click.stop="setCollapsed(false)"><AtlasIcon name="chevrons-up-down" :size="14" /></button>
      </div>
      <template v-else>
        <div class="board-group-title">
          <button v-if="ctx.canEdit.value" type="button" class="board-group-toggle nodrag" :aria-label="t.node.collapse" :title="t.node.collapse" data-board-group-collapse
            @click.stop="setCollapsed(true)"><AtlasIcon name="chevrons-down-up" :size="14" /></button>
          <BoardText :id="id" :editing="editing" :binding="binding" :text="item.text" editor-class="board-frame-editor" text-class="board-frame-name" inline :placeholder="t.node.groupName" :aria-label="editLabels.group"><span class="board-frame-name">{{ t.node.group }}</span></BoardText>
        </div>
        <div class="board-group-body" aria-hidden="true" />
        <div v-if="ctx.tool.value === 'select'" class="board-frame-edge" aria-hidden="true" />
      </template>
    </template>
    <div v-else class="board-card-frame">
      <!-- „Náhled“ renders the page (or one section of it) live once it is near the screen; an edited one always. Its text is edited in place. -->
      <PagePreview v-if="preview && item.targetResourceId" :page-id="item.targetResourceId" :anchor="options.anchor ?? null"
        :visible="editing || (nearView && !ctx.overview.value)" :editing="editing" :at="editing ? ctx.editPoint.value : undefined" @whole-page="ctx.restyle(id, { props: { anchor: null } })" @done="ctx.endEdit(id)" />
      <!-- A deployment's product or service (plan 3.4 §5), drawn live from the page's requirements. -->
      <DeploymentBoardCard v-else-if="kind === 'page' && (options.display === 'product' || options.display === 'service') && item.targetResourceId && options.anchor"
        :page-id="item.targetResourceId" :display="options.display" :anchor="options.anchor" />
      <PageCard v-else-if="kind === 'page'" :item="item" :target="item.target" @open="ctx.open(item)" />
      <BoardBlock v-else-if="kind === 'block'" :tone="item.color" :overview="ctx.overview.value" :preview-height="layout === 'flow' ? ctx.storedHeight(id) : undefined" :block="item.props?.block ?? null" :editing="editing" :at="editing ? ctx.editPoint.value : undefined" :host="ctx.blockHost" :readonly="!ctx.canEdit.value" :fit="layout === 'fill'"
        @commit="ctx.commitBlock(id, $event)" @save="ctx.saveBlock(id, $event)" @unsaved="ctx.unsaved(id, $event)" @cancel="ctx.endEdit(id)" @remove="ctx.remove(id)" />
      <BoardLinks v-else-if="kind === 'links' && links" :show="options.show ?? 'people'" :object-id="options.objectId ?? null" :space-id="links.spaceId()" :api="links.api"
        :editing="editing" @done="ctx.endEdit(id)" @scope="ctx.restyle(id, { props: { objectId: $event } })" @open="ctx.openTarget" />
      <ObjectCard v-else-if="kind === 'object'" :item="item" :editing="editing" :host="ctx.blockHost" @done="ctx.endEdit(id)" @edited="ctx.editedObject(id, $event)" />
      <BoardBlockFallback v-else />
    </div>
    <!-- Colleagues at the card's object through another view (objects spec §4.1), the page's avatars and colours. -->
    <CollaboratorAvatars v-if="item.editors?.length" class="board-node-editors nodrag" :peers="item.editors" :label="t.node.whoEdits" />

    <!-- A component that flows is resized by its width only: its height follows its content. -->
    <template v-if="resizable && layout === 'flow'">
      <NodeResizeControl v-for="side in (['left', 'right'] as const)" :key="side" :node-id="id" :position="side" :variant="ResizeControlVariant.Line" :min-width="min.width" class="board-resize-line board-resize-side"
        @resize="onResize" @resize-end="onResizeEnd" />
    </template>
    <NodeResizer v-else-if="resizable" :min-width="min.width" :min-height="min.height" :keep-aspect-ratio="kind === 'note' && !ctx.shift.value"
      handle-class-name="board-resize-handle" line-class-name="board-resize-line" @resize="onResize" @resize-end="ctx.resized(id)" />
    <!-- After the resizer, so the middle of an edge starts a connector and the rest of it resizes. -->
    <!-- Read-only, a connector still needs its sides to attach to: without them Vue Flow ran every line bottom to top, in loops. The same
         handles serve both: switching the right to write (the lock) changes their props, it mounts no handle on every card again. -->
    <!-- Not read from the right to write: every card would draw again when it switches (the lock). A reader is never editing. -->
    <template v-if="!editing && data.motion?.kind !== 'leave'">
      <Handle v-for="side in sides" :id="side" :key="side" type="source" :position="side" class="board-handle" :data-side="side" />
      <!-- With the connector tool the whole item starts a connector (automatic sides). -->
      <Handle v-if="ctx.tool.value === 'connector'" id="auto" type="source" :position="Position.Top" class="board-handle-auto" />
    </template>

  </div>
</template>

<style scoped>
.board-node { position: relative; width: 100%; height: 100%; display: flex; color: var(--text); border-radius: var(--atlas-radius-md);
  outline: 2px solid transparent; outline-offset: 2px;
  transition: outline-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out), border-color var(--atlas-motion-fast) var(--atlas-ease-out),
    opacity var(--atlas-motion-fast) var(--atlas-ease-out), scale var(--atlas-motion-surface) var(--atlas-ease-out); }
.board-node.is-selected { outline-color: var(--state-selected, var(--accent)); }
/* The drop target of a drag (a frame or group: its area tints) or of a connector (an item: its outline), J1. */
.board-node.is-drop-target { outline-color: var(--accent); }
.board-node.is-drop-target :is(.board-frame-body, .board-group-body) { border-color: var(--accent); background: color-mix(in srgb, var(--accent) 10%, var(--appearance-fill, var(--appearance-bg, var(--surface))) 45%); }
/* Neighbour highlighting (objects spec §5.3): up in the accent, down in amber, the rest faded; only colour and opacity change. */
.board-canvas[data-highlighting] .board-node:not([data-highlight]) { opacity: var(--board-dim-node, .3); }
.board-node:is([data-highlight="self"], [data-highlight="up"], [data-highlight="path"]) { outline-color: var(--accent); }
.board-node[data-highlight="down"] { outline-color: var(--warning, var(--accent)); }
.board-node.is-leaving { pointer-events: none; }
.board-placeholder { color: var(--muted); }
.board-node[data-kind="note"] { border: 1px solid color-mix(in oklab, var(--appearance-fg, var(--line)) 22%, transparent); background: var(--appearance-bg, var(--surface)); box-shadow: var(--refresh-card-shadow); }
@media (hover: hover) and (pointer: fine) { .board-node[data-kind="note"]:hover { box-shadow: var(--refresh-card-shadow-hover); } }
.board-note { display: flex; width: 100%; padding: 12px; overflow: hidden; }
.board-free-text { display: flex; width: 100%; padding: 4px 2px; color: var(--appearance-ink, var(--text)); }
.board-node[data-kind="shape"] { background: transparent; }
.board-shape { position: absolute; inset: 0; width: 100%; height: 100%; overflow: visible; }
.board-shape :is(rect, ellipse, polygon) { fill: var(--appearance-bg, var(--surface)); stroke: var(--appearance-fg, var(--line)); stroke-width: 1.5; }
.board-shape-text { position: relative; display: flex; align-items: center; width: 100%; padding: 12px 16px; }
.board-node[data-shape="diamond"] .board-shape-text { padding: 22% 24%; }
/* A translucent layer of a tone outside sRGB paints pure yellow pixels (measured in Chromium on the dashed edge of a blue-2 or
   violet-2 group; color-mix does not clamp, in oklab or in srgb), so the fills of frames, groups and the drop target take the tone
   clamped into the gamut. The colour itself is the same. */
@supports (color: rgb(from red r g b)) {
  .board-node { --appearance-fill: rgb(from var(--appearance-bg, var(--surface)) clamp(0, r, 255) clamp(0, g, 255) clamp(0, b, 255)); }
}
.board-node[data-kind="frame"] { border-radius: var(--atlas-radius-lg); }
.board-frame-body { position: absolute; inset: 0; border: 1.5px solid color-mix(in oklab, var(--appearance-fg, var(--line)) 45%, transparent); border-radius: inherit;
  background: color-mix(in srgb, var(--appearance-fill, var(--appearance-bg, var(--surface))) 62%, transparent); pointer-events: none;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
/* The frame rule (BoardCanvas onPaneClick): the outline always takes the pointer, a ring 12 units wide around the border (the
   clip-path's hole lets the inside through); a selected frame's or group's whole area does, so a drag inside moves it. */
.board-frame-edge { position: absolute; inset: -6px; border-radius: inherit; pointer-events: auto; cursor: move;
  clip-path: polygon(evenodd, 0 0, 100% 0, 100% 100%, 0 100%, 0 0, 12px 12px, 12px calc(100% - 12px), calc(100% - 12px) calc(100% - 12px), calc(100% - 12px) 12px, 12px 12px); }
.board-node.is-selected:is([data-kind="frame"], [data-kind="group"]) :is(.board-frame-body, .board-group-body) { pointer-events: auto; cursor: move; }
/* A frame's or open group's name stands above its box in the node's own flow (a row of no height, the name at its end), not
   positioned: a positioned name outside the box was a paint chunk of its own, which kept Chrome from flattening a dimmed node's
   opacity (the neighbour highlighting) into the board's layer, so every card over a group became a compositing layer (707 on a
   large board, most of a frame's work while typing). */
.board-node:is([data-kind="frame"], [data-kind="group"]:not([data-collapsed])) { display: grid; grid-template: 0 / minmax(0, 1fr); align-items: end; justify-items: start; }
.board-frame-title { display: flex; align-items: center; gap: 6px; max-width: 100%; margin-bottom: 6px; pointer-events: auto; cursor: grab; }
/* The frame's colour on its name: a swatch and the name in its ink, readable in both themes. */
.board-frame-title::before, .board-group-title::after { content: ""; flex: none; width: 10px; height: 10px; border-radius: 3px; background: var(--appearance-ink, var(--muted));
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.board-group-title::after { order: -1; }
.board-frame-name { display: block; overflow: hidden; color: color-mix(in oklab, var(--appearance-fg, var(--muted)) 80%, var(--text)); font-size: 14px; font-weight: 600; text-overflow: ellipsis; white-space: nowrap;
  transition: color var(--atlas-motion-fast) var(--atlas-ease-out); }
.board-frame-editor { width: 240px; font-size: 14px; font-weight: 600; }
.board-node[data-kind="group"] { border-radius: var(--atlas-radius-lg); }
.board-group-title { display: flex; align-items: center; gap: 4px; max-width: 100%; margin-bottom: 6px; pointer-events: auto; cursor: grab; }
.board-group-body { position: absolute; inset: 0; border: 1.5px dashed color-mix(in oklab, var(--appearance-fg, var(--line)) 60%, transparent); border-radius: inherit;
  background: color-mix(in srgb, var(--appearance-fill, var(--appearance-bg, var(--surface))) 22%, transparent); pointer-events: none;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.board-group-card { display: flex; align-items: center; gap: 8px; width: 100%; padding: 8px 10px 8px 14px; border: 1.5px solid color-mix(in oklab, var(--appearance-fg, var(--line)) 60%, transparent);
  border-radius: inherit; background: var(--appearance-bg, var(--surface)); box-shadow: var(--refresh-card-shadow); animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out);
  transition: box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .board-group-card:hover { box-shadow: var(--refresh-card-shadow-hover); } }
.board-group-card-name { flex: 1; min-width: 0; overflow: hidden; font-size: 14px; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.board-group-count { flex: none; padding: 1px 8px; border-radius: 999px; background: color-mix(in oklab, var(--appearance-fg, var(--muted)) 16%, var(--surface)); color: var(--muted); font-size: 12px; font-weight: 600; }
.board-group-toggle { display: inline-grid; flex: none; place-items: center; width: 22px; height: 22px; padding: 0; border: 0; border-radius: 6px; background: transparent; color: var(--muted); cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .board-group-toggle:hover { background: var(--hover); color: var(--text); } }
.board-group-toggle:active { scale: var(--atlas-press); }
.board-group-toggle:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
/* White and black have no hue in the appearance palette. */
.board-node[data-board-color="white"] { --appearance-bg: var(--surface); --appearance-fg: var(--muted); --appearance-ink: var(--text); }
/* Black is the text colour turned background: its ink is the surface's, in both themes (J1: the text and caret were invisible). */
.board-node[data-board-color="black"] { --appearance-bg: color-mix(in oklab, var(--text) 92%, var(--surface)); --appearance-fg: var(--surface); --appearance-ink: var(--surface); color: var(--surface); }
/* A card (page, editor block) fills a framed node and is clipped by it; the handles stay outside the clip. */
.board-card-frame { position: relative; width: 100%; height: 100%; overflow: hidden; border: 1px solid var(--line); border-radius: inherit; background: var(--surface); box-shadow: var(--refresh-card-shadow);
  color: var(--text);
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-slow) var(--atlas-ease-out); }
/* A coloured card (YouTrack's: owner, 27. 10.): a stripe down its left edge in the colour's ink and the colour as a light tint; the
   text keeps the surface's ink, readable in both themes. */
.board-card-frame::before { content: ""; position: absolute; inset: 0 auto 0 0; z-index: 1; width: 4px; background: var(--board-card-stripe, transparent); pointer-events: none;
  transition: background-color var(--atlas-motion-slow) var(--atlas-ease-out); }
.board-node[data-board-color] .board-card-frame { --board-card-stripe: var(--appearance-ink); border-color: color-mix(in oklab, var(--appearance-fg) 28%, var(--line));
  background: color-mix(in oklab, var(--appearance-bg) 34%, var(--surface)); }
.board-node[data-board-color="white"] .board-card-frame { --board-card-stripe: transparent; border-color: var(--line); background: var(--surface); }
.board-node[data-board-color="black"] .board-card-frame { --board-card-stripe: var(--text); border-color: var(--line); background: var(--surface); }
/* An editor component lies on the board without a card (owner, 28. 10.): transparent by default, its colour a whole background
   with the colour's own text, light on the dark tones; hover and selection are the node's outline. It never clips what it flows. */
.board-node[data-block-layout] .board-card-frame { border: 1px solid transparent; background: var(--board-block-bg); color: var(--board-block-fg); box-shadow: none; overflow: visible;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
/* The colour pair (blocks/boardBlockLayouts.css) fades as one, with the node's own transitions kept. */
.board-node[data-block-layout] {
  transition: outline-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out), border-color var(--atlas-motion-fast) var(--atlas-ease-out),
    opacity var(--atlas-motion-fast) var(--atlas-ease-out), scale var(--atlas-motion-surface) var(--atlas-ease-out),
    --board-block-bg var(--atlas-motion-slow) var(--atlas-ease-out), --board-block-fg var(--atlas-motion-slow) var(--atlas-ease-out);
}
.board-node[data-block-layout="fill"] .board-card-frame { overflow: hidden; }
.board-node[data-block-layout] .board-card-frame::before { content: none; }
/* A card's inner room (boardCard.css .bcard), eased in with the colour. */
.board-node[data-block-layout][data-board-color] .board-card-frame { padding: 12px 14px; }
/* White is the board's own surface: a hairline and its shadow keep it apart, in the dark mode too. */
.board-node[data-block-layout][data-board-color="white"] .board-card-frame { border-color: var(--line); box-shadow: var(--refresh-card-shadow); }
@media (hover: hover) and (pointer: fine) {
  .board-node[data-block-layout]:not(.is-selected):hover { outline-color: color-mix(in oklab, var(--accent) 35%, transparent); }
  .board-node[data-block-layout]:not([data-board-color="white"]):hover .board-card-frame { box-shadow: none; }
}
/* While a text or block is edited, nothing clips: the editor's selection bubble and the plugin's panel live inside it. */
.board-node.is-editing :is(.board-note, .board-free-text, .board-shape-text, .board-card-frame) { overflow: visible; }
.board-node-editors { position: absolute; top: -14px; right: 8px; z-index: 1; }
.board-node.is-missing .board-card-frame { border-style: dashed; background: var(--hover); box-shadow: none; }
@media (hover: hover) and (pointer: fine) { .board-node:hover .board-card-frame { box-shadow: var(--refresh-card-shadow-hover); } }
</style>

<style>
/* Vue Flow's own elements inside a board node: handles that fade in on hover in room they already have, resize handles. */
.board-node .board-handle { width: 12px; height: 12px; border: 2px solid var(--accent); border-radius: 999px; background: var(--surface); opacity: 0;
  transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out), scale var(--atlas-motion-fast) var(--atlas-ease-out); }
/* Read only (a reader, the lock: one class on the canvas, nothing changes on the cards): a handle is only an anchor of the connectors. */
:global(.board-canvas.is-readonly) .board-node .board-handle { min-width: 0; min-height: 0; width: 1px; height: 1px; border: 0; opacity: 0; pointer-events: none; }
.board-node .board-handle-auto { inset: 0; width: 100%; height: 100%; min-width: 0; min-height: 0; transform: none; border: 0; border-radius: inherit; background: transparent; opacity: 0; cursor: crosshair; }
@media (hover: hover) and (pointer: fine) {
  .vue-flow__node:hover .board-node .board-handle { opacity: 1; }
  .board-node .board-handle:hover { scale: 1.25; }
}
/* Shown on the only selected item, as its resize handles are: on every item of a select all (450) they faded in by the thousand,
   most of the cost of selecting (UX SB4-3); hover still shows them on any item. */
.board-node.is-single .board-handle, .board-canvas.is-connecting .board-node .board-handle { opacity: 1; }
/* Nor on a card the neighbour highlighting dims: its handles sticking out of its box kept its opacity from flattening into the
   board's layer, as a group's name did (one compositing layer per card). A dimmed card is not the one under the pointer. */
.board-canvas[data-highlighting] .board-node:not([data-highlight], .is-single) .board-handle { visibility: hidden; }
.board-node .board-resize-handle { width: 10px; height: 10px; border: 1.5px solid var(--accent); border-radius: 3px; background: var(--surface); animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
.board-node .board-resize-line { border-color: var(--accent); }
/* The side edges of a component that flows are its only resize controls: a strip wider than their line takes the pointer. */
.board-node .board-resize-side::before { content: ""; position: absolute; inset: 0 -5px; }
.board-node:is([data-kind="frame"], [data-kind="group"]) :is(.vue-flow__resize-control, .board-handle) { pointer-events: all; }
/* Members of a collapsed group stay drawn and fade (and, with motion, shrink) away; expanding brings them back the same way. */
.vue-flow__node:has(> .board-node) { transition: opacity var(--atlas-motion-surface) var(--atlas-ease-out); }
.vue-flow__node.is-collapsed-away { opacity: 0; pointer-events: none; }
.vue-flow__node.is-collapsed-away .board-node { scale: .96; }
@media (prefers-reduced-motion: reduce) { .vue-flow__node.is-collapsed-away .board-node { scale: none; } }
[data-atlas-reduced-motion] .vue-flow__node.is-collapsed-away .board-node { scale: none; }
</style>
