<script lang="ts">
import { lazyComponent, loadChunk } from "../../lazyComponent";
// The chunks' loaders live in the module, not in a board's setup: Vite's preload helper keeps a loader (in its stylesheet's error
// listener) for the life of the tab, and one made in setup kept the first board opened with it (heap snapshots, 4.0).
const loadCommandPanel = () => import("./canvas/BoardCommandPanel.vue");
const BoardCommandPanel = lazyComponent(loadCommandPanel);
const BoardBlock = lazyComponent(() => import("./blocks/BoardBlock.vue"));
const editorBlocks = () => loadChunk(() => import("../../boards/editorBlocks"));
</script>

<script setup lang="ts">
import "@vue-flow/core/dist/style.css";
import "@vue-flow/node-resizer/dist/style.css";
import "@vue-flow/minimap/dist/style.css";
import { ConnectionMode, SelectionMode, useVueFlow, VueFlow, type Connection, type ConnectionLineProps, type EdgeChange, type EdgeTypesObject, type GraphEdge, type GraphNode, type NodeChange, type NodeTypesObject } from "@vue-flow/core";
import { MiniMap } from "@vue-flow/minimap";
import { computed, markRaw, nextTick, onBeforeUnmount, onMounted, provide, ref, shallowRef, useId, watch, type Ref } from "vue";
import { landFocus, opensActions } from "../../keyboard/navigation";
import { objectCardViews } from "../../boards/objectViews";
import { liveObjects } from "../../objects/liveObjects";
import { geometryOf } from "../../boards/geometry";
import { defaultBoardSettings, maxBatchChanges, type BoardBlockNode, type BoardChange, type BoardColor, type BoardEdit, type BoardLinksShow, type ObjectEdit, type BoardPropsPatch, type BoardSettings, type BoardSide, type Geometry } from "../../boards/boardApi";
import { resolvePasteItems, type BoardInsert, type PickedObject, type YouTrackIssueSnapshot } from "../../boards/boardBlockNodes";
import { boardClipboardType, copyPayload, pasteChanges, readPayload } from "../../boards/boardClipboard";
import { dropAccepts, placedContacts, readDrop } from "../../boards/boardDrop";
import { contactRemoved, contactsRestored, repeatedContact } from "../../contacts/contactLinking";
import {
  alignChanges, blockLayoutOf, boundsOf, connectChanges, connectToNewNoteChanges, createChange, defaultProps, deleteChanges, distributeChanges, duplicateChanges, fitGeometry,
  insertChanges, itemLabel, minSizeOf, moveChanges, nearestSide, noteToPageChanges, previewSides, propsOf, reconnectChanges, reorderChanges, reuseUnchanged, sideMiddle, styleChanges, textChanges, toFlowEdges, toFlowNodes, topZ,
  type Alignment, type BoardEdge as FlowEdge, type BoardNode as FlowNode, type Point,
} from "../../boards/boardFlow";
import type { MarkdownIntention, MarkdownIntentionsMenu } from "@atlas/markdown/core";
import { actionCommands, boardCommands, itemCommands, onlyCards, type ItemOffer } from "../../boards/boardCommands";
import { boardIntentionRows, hasBoardIntentions } from "../../boards/boardIntentions";
import { readPageTarget } from "../../boards/boardTargets";
import { editPage } from "../../boards/pageEditing";
import { useEditorTheme } from "../../editor/editorSetup";
import { boardLink, pageLink } from "../../pageLinks";
import { actionsMenuReady, loadActionsMenu } from "./canvas/actionsMenuChunk";
import { boardToolKeys } from "../../boards/boardKeymap";
import { arrangeChanges, COLLAPSED_GROUP, collapsedOwners, containerAt, dropTargets, dropTargetsOf, escapeSelection, groupChanges, membersOf, ungroupChanges, withMembers } from "../../boards/boardGroups";
import { loadConnectorStyle, rememberConnectorStyle, saveConnectorStyle, type ConnectorStyle } from "../../boards/boardConnectorStyle";
import type { Port } from "../../boards/boardRoute";
import { linkOf, neighbourhood, pathBetween, type Highlight } from "../../boards/boardNeighbours";
import type { BoardItem } from "../../boards/boardModel";
import { planGroupMotion, planMotion } from "../../boards/boardMotion";
import { appPasteContext, parsePaste, type PasteContext, type PasteItem } from "../../boards/boardPaste";
import type { BoardBlockHost, EditorBlockModule } from "../../boards/editorBlocks";
import { whenIdle } from "../../idle";
import { preheatTextEditor } from "./text/textEditorChunk";
import { freeSpot, nearestInDirection, readingOrder, type Direction } from "../../boards/boardPlacement";
import type { BoardViewItem, PresencePayload, RemoteDrag } from "../../boards/boardSession";
import { snapMove, snapResize, type GridSnap, type Guide } from "../../boards/boardSnap";
import { timelineTicks, xOfDate } from "../../boards/boardTimeline";
import { readBoardPlace, saveBoardPlace } from "../../boards/boardPlace";
import { cubicBezier, fitViewport, visibleObjects, wheelViewport, zoomAt, zoomStep, type Viewport } from "../../boards/boardViewport";
import { animateAtlas, literalMs, motionEasing, motionMs, prefersReducedMotion } from "../../motion/aiMotion";
import type { PlayedMotion } from "./canvas/boardAnimations";
import BoardBackground from "./canvas/BoardBackground.vue";
import { boardCanvasKey, only, perItem, type BoardCanvasContext, type BoardTool } from "./canvas/boardCanvasContext";
import BoardConnectorPreview from "./canvas/BoardConnectorPreview.vue";
import BoardContextToolbar from "./canvas/BoardContextToolbar.vue";
import BoardEdge from "./canvas/BoardEdge.vue";
import BoardGuides from "./canvas/BoardGuides.vue";
import BoardNode from "./canvas/BoardNode.vue";
import BoardPresence from "./canvas/BoardPresence.vue";
import BoardSettingsPopover from "./canvas/BoardSettingsPopover.vue";
import BoardToolRail from "./canvas/BoardToolRail.vue";
import BoardZoomControls from "./canvas/BoardZoomControls.vue";
import { isBoardCard } from "../../boards/objectViews";
import type { EditPoint } from "../../boards/boardCaret";
import type { EditorView } from "@tiptap/pm/view";
import { useToast } from "../../composables/useToast";
import t from "../../i18n/boards";
import { drafts } from "../../lib/unsavedWork";

/**
 * The board surface (Atlas 3, module Boards), on Vue Flow in
 * controlled mode: nodes and edges are derived from `items` (the optimistic view of the board model) and from the local state of
 * my own gesture; Vue Flow never owns the data. Every finished gesture (drag, resize, nudge, connect, reconnect, delete, text,
 * style, order, paste, drop) leaves as `change` with one batch; a drag in progress stays local and goes out as `preview`
 * (presence) only, with my pointer. Read-only (`canEdit` false) keeps navigation, selection, copying and Otevřít.
 *
 * Why its own component: nothing else in Atlas positions content freely on a zoomable plane (PageTree and the lists own an order,
 * the editor's blocks flow in a document). The sub-components under canvas/ are its private parts.
 */
export interface PickedPage { id: string; title: string; anchor?: string }
const props = withDefaults(defineProps<{
  items: readonly BoardViewItem[];
  /** The board's name, the accessible name of the surface. */
  label: string;
  canEdit?: boolean;
  remote?: readonly RemoteDrag[];
  canUndo?: boolean;
  canRedo?: boolean;
  settings?: BoardSettings;
  /** The page picker; null when the user cancels. */
  pickPage?: () => Promise<PickedPage | readonly PickedPage[] | null>;
  /** Editor blocks of the YouTrack issue picker (I). */
  pickIssues?: () => Promise<readonly BoardBlockNode[] | null>;
  /** Directory contacts to place as live object cards (objects spec §5.1). */
  pickContacts?: () => Promise<readonly PickedObject[] | null>;
  /** How pasted and dropped text is read (boardPaste.ts); the running application's by default. */
  pasteContext?: PasteContext;
  /** The host's issue lookup in the board's space; without it a pasted issue reference stays a note. */
  resolveIssue?: (issueId: string) => Promise<YouTrackIssueSnapshot | null>;
  /** Host services of the editor blocks' plugins in the board's space (editorBlocks.ts); none in the gallery. */
  blockHost?: BoardBlockHost;
  /** Creates a page of a note's Markdown („Převést na stránku“); null when it did not happen. */
  createPage?: (markdown: string) => Promise<PickedPage | null>;
  /** An item to have selected when the board opens and whenever this changes (the relations graph's subject); the person may select on. */
  preselect?: string | null;
  /** The surface's accessible description when it is not a board's (the relations graph); a board's own by default. */
  ariaLabel?: string;
  /** Fitting never zooms out past this (a small read-only graph stays legible, its overflow is panned to). */
  fitMinZoom?: number;
  /** The board's id: with it the view and selection are kept for the way back (boardPlace.ts). */
  placeKey?: string;
}>(), { preselect: null, canEdit: false, remote: () => [], canUndo: false, canRedo: false, settings: () => defaultBoardSettings, pickPage: undefined, pickIssues: undefined, pickContacts: undefined, pasteContext: undefined, resolveIssue: undefined, blockHost: undefined, createPage: undefined });
/** `openDetail`: the object detail of a page card (its item id), or of the board itself (null). */
/** `change`'s `coalesce`: the editing whose saves (each pause in typing) are one undo step. */
const emit = defineEmits<{ change: [changes: BoardEdit[], coalesce?: string]; preview: [payload: PresencePayload]; undo: []; redo: []; openPage: [pageId: string, heading?: string, block?: string]; openObject: [id: string, typeId: string]; openDetail: [itemId: string | null]; viewport: [objectIds: string[]]; unsaved: [holding: boolean]; selection: [ids: string[]] }>();

const flowId = `board-${useId()}`;
const flow = useVueFlow(flowId);
const surface = ref<HTMLElement>();
// Their props are the part of Vue Flow's node and edge props they read.
const nodeTypes = { board: markRaw(BoardNode) } as unknown as NodeTypesObject;
const edgeTypes = { board: markRaw(BoardEdge) } as unknown as EdgeTypesObject;
const viewport = computed<Viewport>(() => flow.viewport.value);
/**
 * Below 40 % a component's text is a few pixels tall: one not drawn yet stays a preview (BoardBlock `overview`), so zooming out to
 * the whole board does not draw every chart, diagram and formula at once (owner, 28. 10.: a fit view of 100 components froze for
 * seconds). Nodes read only this flag, never the zoom itself. It also holds until the board has its opening view (the nodes mount at
 * 100 % before the fit) and while the view travels: what a travel passes is not drawn, only what it arrives at.
 */
const opened = ref(false), travelling = ref(false);
let travelTimer: ReturnType<typeof setTimeout> | undefined;
const overview = computed(() => !opened.value || travelling.value || viewport.value.zoom < 0.4);
const travel = cubicBezier(0.3, 0.2, 0.1, 1);
const newId = () => crypto.randomUUID();

// State of the interaction. The data is `items`; this is only what the user is doing with it right now.
const tool = ref<BoardTool>("select");
const selected = shallowRef<ReadonlySet<string>>(new Set());
const editing = ref<string>();
/** Geometry under my own gesture until the batch comes back as items. */
const local = shallowRef<ReadonlyMap<string, Geometry>>(new Map());
const guides = shallowRef<readonly Guide[]>([]);
const motion = shallowRef<ReadonlyMap<string, PlayedMotion>>(new Map());
/** Items gone from `items` that still fade out on screen (the model is not held up). */
const leaving = shallowRef<ReadonlyMap<string, BoardViewItem>>(new Map());
const keys = ref({ shift: false, alt: false, space: false });
/** What the gesture under way would land in (the frame of a drop, the item of a connector), highlighted on the board. */
const landing = shallowRef<ReadonlySet<string>>(new Set());
function showDropTarget(next: ReadonlySet<string>) {
  if (next.size !== landing.value.size || [...next].some(id => !landing.value.has(id))) landing.value = next;
}
/** Connecting from the keyboard (L on one selected item, J1): the item it starts at and the one the arrows chose. Moving an end of the
 * selected connector (L its end, Shift+L its start): `edge` and the end that moves; `from` is the end that stays. */
const linking = shallowRef<{ from: string; to?: string; edge?: string; end?: "source" | "target" } | null>(null);
const minimap = ref(false);
const settingsOpen = ref(false);
const connecting = ref(false);
let cursor: Point | undefined;

/**
 * The height a component that flows has on screen (Vue Flow measures it, boardFlow.blockLayoutOf): every geometry here (fit, the
 * toolbar, snapping, align, groups and frames, Tab) uses it, and the person's own move or resize of the item stores it. A height is
 * never sent on its own: two clients measuring a pixel apart would correct each other for ever.
 */
const measured = shallowRef<ReadonlyMap<string, number>>(new Map());
// The same object while its item and height stay: one component settling does not draw every other one anew (boardFlow's nodeParts).
const overlaid = new WeakMap<BoardViewItem, BoardViewItem>();
const items = computed<readonly BoardViewItem[]>(() => (measured.value.size ? props.items.map(item => {
  const height = measured.value.get(item.id);
  if (height === undefined || height === item.height || blockLayoutOf(item) !== "flow") return item;
  const known = overlaid.get(item);
  if (known?.height === height) return known;
  const shown = { ...item, height };
  overlaid.set(item, shown);
  return shown;
}) : props.items));
const byId = computed(() => new Map(items.value.map(item => [item.id, item])));
// A preview stands at the stored height: at the measured one, which holds its own frame, it would grow by that frame each time.
const storedHeights = computed(() => new Map(props.items.map(item => [item.id, item.height])));
const cards = computed(() => items.value.filter(item => item.kind !== "connector"));
const selectedItems = computed(() => items.value.filter(item => selected.value.has(item.id)));
/**
 * A value every node or edge reads (the one selected item, the highlighting), set only when it changes: as a computed, each change of
 * the selection (every step of a marquee) had every node and edge check it again, though it stayed the same.
 */
function steady<T>(source: () => T): Readonly<Ref<T>> {
  const value = shallowRef(source());
  watch(source, next => { value.value = next; }, { flush: "sync" });
  return value;
}
const single = steady(() => (selected.value.size === 1 ? [...selected.value][0] : undefined));
const displayed = computed(() => (leaving.value.size ? [...items.value, ...leaving.value.values()] : items.value));
// While a drag runs, the nodes are not derived anew on every frame (Vue Flow would sync all of them): the dragged ones get their
// position in the store directly, and the derivation reads the drag's geometry without depending on it.
const dragActive = ref(false);
let dragGeometry: ReadonlyMap<string, Geometry> = new Map();
/** The colour an item shows (owner, 27. 10.: nothing all white): its own; a frame's neutral grey. */
function toneOf(item: BoardViewItem): BoardColor | undefined {
  return item.color ?? (item.kind === "frame" ? "gray" : undefined);
}
/** The cards a connector's route goes around: everything drawn but connectors, frames, open groups and hidden members. */
const obstacles = computed(() => {
  const hidden = collapsedOwners(items.value);
  return items.value.filter(item => item.kind !== "connector" && item.kind !== "frame" && !(item.kind === "group" && !item.props?.collapsed) && !hidden.has(item.id))
    .map(item => ({ id: item.id, ...geometryOf(item), ...(item.kind === "group" && COLLAPSED_GROUP) }));
});
/** The look the next new connector takes: the last one this person gave a connector (per browser). */
let connectorStyle = loadConnectorStyle();
/** An object card is named after its object („Kontakt: Jana Nováková“); none while the object has not come. */
function objectLabel(item: BoardItem) {
  const title = liveObjects.get(item.targetResourceId ?? "")?.title;
  return title ? `${objectCardViews[item.props?.typeId ?? ""]?.label ?? t.canvas.object}: ${title}` : undefined;
}
// The right to write is no input of the nodes and edges (the canvas's own Vue Flow props decide it): switching it (the lock) derives
// none of them again. Nor are the selection and the connector under the pointer or with the focus: they are derived without them and
// take their flags after (`flaggedAs`), so that selecting (a marquee's every step, select all) or hovering derives nothing anew.
const unselected: ReadonlySet<string> = new Set();
/** The connector under the pointer (its popover) and the one with the keyboard focus: both get their end handles, as a selected one. */
const hoveredEdge = ref<string>(), focusedEdge = ref<string>();
const shownGeometry = () => (dragActive.value ? dragGeometry : local.value);
const derivedNodes = computed<FlowNode[]>(previous => {
  const flow = toFlowNodes(displayed.value, { selected: unselected, editing: editing.value, motion: motion.value, local: shownGeometry() });
  return reuseUnchanged(previous, flow.map(node => {
    const label = node.data!.item.kind === "object" ? objectLabel(node.data!.item) : undefined;
    return label ? { ...node, ariaLabel: label } : node;
  }));
});
/** The same node or edge with these flags, the same object for as long as they stay (`feed` then passes it by). */
const flagged = new WeakMap<object, Map<string, object>>();
function flaggedAs<T extends FlowNode | FlowEdge>(entry: T, selected: boolean, updatable?: boolean): T {
  const now = entry as { selected?: boolean; updatable?: boolean };
  if (!!now.selected === selected && (updatable === undefined || !!now.updatable === updatable)) return entry;
  const key = `${selected}:${updatable}`;
  let variants = flagged.get(entry);
  if (!variants) flagged.set(entry, variants = new Map());
  let other = variants.get(key) as T | undefined;
  if (!other) variants.set(key, other = { ...entry, selected, ...(updatable !== undefined && { updatable }) });
  return other;
}
// What cannot be selected now (an item leaving) is drawn unselected.
const nodes = computed(() => derivedNodes.value.map(node => flaggedAs(node, node.selectable !== false && selected.value.has(node.id))));
const restingEdges = computed<FlowEdge[]>(previous => reuseUnchanged(previous, toFlowEdges(displayed.value, { selected: unselected, motion: motion.value, local: shownGeometry() })));
/**
 * A frame of a drag or resize derives anew only the connectors attached to what moves (a group's members move with it, and a connector
 * merged into a collapsed group is attached to the member it leaves): their sides follow the moved boxes, every other one stays as it is.
 */
const movingEdges = computed<ReadonlyMap<string, FlowEdge> | undefined>(() => {
  const moved = local.value;
  if (!dragActive.value || !moved.size) return undefined;
  const attached = displayed.value.filter(item => item.kind !== "connector" || moved.has(item.from ?? "") || moved.has(item.to ?? ""));
  const derived = reuseUnchanged(restingEdges.value, toFlowEdges(attached, { selected: unselected, motion: motion.value, local: moved }));
  return new Map(derived.map(edge => [edge.id, edge]));
});
// A connector's end handles where a person can reach for them: selected, under the pointer or focused (boardFlow.ts); a merged one has none.
const edges = computed(() => {
  const moving = movingEdges.value, hovered = hoveredEdge.value, focused = focusedEdge.value;
  return restingEdges.value.map(edge => {
    const on = selected.value.has(edge.id);
    return flaggedAs(moving?.get(edge.id) ?? edge, on, edge.data?.merged ? undefined : on || edge.id === hovered || edge.id === focused);
  });
});
/** The level of the connectors among the items, for their labels' layer. */
const edgeLevel = computed(() => restingEdges.value[0]?.zIndex ?? 0);
// Fed to the store directly: Vue Flow ignores a change of its nodes prop for a tick after each of its own syncs, which lost a
// selection made right after a change of the items.
// The same nodes in the same order (a colleague moved or restyled some): only the changed ones are assigned onto their graph nodes, as
// setNodes would, which walks every node against every other (COL2-1).
// Edges the same way: a drag frame or a selection changes a few of them, and setEdges parsed and drew all of them again (≈ 300 ms per
// frame of a drag at 4× CPU on 200 connectors). An edge whose ends changed goes through setEdges: Vue Flow resolves its end
// nodes there. A side that flips (a connector's end dragged round its card) is assigned as the rest: Vue Flow draws the edge from its
// handle id, and its per-handle connection index (setEdges rebuilds it for every edge) is read by nothing here.
// The same ones in another order, or with new ones besides them (a slice of an opening board, an item someone created, a frame lifted
// by a drag): Vue Flow parses only the new ones (add, applied in onNodesChange and onEdgesChange) and the store takes the order of
// `next` (an edge's is its z among connectors at the same level); setNodes looked every node's parent up in the whole list (3.7 s to
// open 1430 items) and setEdges drew every edge again. Something gone is taken out on its own (a delete, an undo of a create): through
// set, a delete on a large board parsed and drew every node and edge again (≈ 90 ms at 1× CPU).
interface FlowStore<T> { find(id: string): object | undefined; set(all: T[]): void; add(added: T[]): void; remove(gone: T[]): void; order(all: readonly T[]): void }
function feed<T extends { id: string }>(next: T[], fed: readonly T[], store: FlowStore<T>, same = (a: T, b: T) => a.id === b.id) {
  const patched = next.length === fed.length && next.every((entry, index) => same(entry, fed[index]!))
    && next.every((entry, index) => { if (entry === fed[index]) return true; const graph = store.find(entry.id); if (graph) Object.assign(graph, entry); return !!graph; });
  if (patched) return;
  const before = new Map(fed.map(entry => [entry.id, entry]));
  const kept = next.filter(entry => before.has(entry.id)), changed = kept.filter(entry => entry !== before.get(entry.id));
  if (!fed.length || !kept.every(entry => same(entry, before.get(entry.id)!)) || !changed.every(entry => store.find(entry.id))) {
    store.set(next);
    return;
  }
  const held = kept.length < fed.length ? new Set(kept.map(entry => entry.id)) : undefined;
  const left = held ? fed.filter(entry => held.has(entry.id)) : fed;
  if (held) store.remove(fed.filter(entry => !held.has(entry.id)));
  for (const entry of changed) Object.assign(store.find(entry.id)!, entry);
  const added = next.filter(entry => !before.has(entry.id));
  if (added.length) store.add(added);
  // The store holds what it kept, then what was added: anything else takes the order of `next`.
  if ([...left, ...added].some((entry, index) => entry.id !== next[index]!.id)) store.order(next);
}
let fed: readonly FlowNode[] = [], fedEdges: readonly FlowEdge[] = [];
/**
 * A large board opens a slice of its nodes per frame (frames and groups first, then the cards in their order) and each connector with its
 * second end, so its first cards show at once: drawing 250 cards in one task held the page for 1.5 s at 4× CPU. A smaller board opens
 * in one go. `shown`: how many of the nodes Vue Flow has, all of them once the board is open.
 * Slices sized by time instead (4.0, measured): Vue Flow draws every node and edge wrapper again whenever a node comes, so a frame costs
 * about as much again as what is already shown; slices small enough for 100 ms frames made the opening of 600 cards 10–30 % longer at 1×
 * and 2.5× longer at 4× CPU.
 */
const OPEN_SLICE = 25, OPEN_SLICE_MAX = 100;
let shown = Infinity, slicing = 0;
const nodeStore: FlowStore<FlowNode> = {
  find: flow.findNode, set: flow.setNodes, add: flow.addNodes, remove: gone => flow.applyNodeChanges(gone.map(node => ({ type: "remove", id: node.id }))),
  order: all => { flow.nodes.value = all.flatMap(node => flow.findNode(node.id) ?? []); },
};
const edgeStore: FlowStore<FlowEdge> = {
  find: flow.findEdge, set: flow.setEdges, add: flow.addEdges,
  remove: gone => flow.applyEdgeChanges(gone.map(edge => ({ type: "remove", id: edge.id, source: edge.source, target: edge.target, sourceHandle: edge.sourceHandle ?? null, targetHandle: edge.targetHandle ?? null }))),
  order: all => { flow.edges.value = all.flatMap(edge => flow.findEdge(edge.id) ?? []); },
};
function feedNodes() {
  const next = nodes.value.length > shown ? nodes.value.slice(0, shown) : nodes.value;
  feed(next, fed, nodeStore);
  fed = next;
}
function feedEdges() {
  const ids = shown === Infinity ? undefined : new Set(fed.map(node => node.id));
  const next = ids ? edges.value.filter(edge => ids.has(edge.source) && ids.has(edge.target)) : edges.value;
  feed(next, fedEdges, edgeStore, (a, b) => a.id === b.id && a.source === b.source && a.target === b.target);
  fedEdges = next;
}
function openNextSlice() {
  slicing = requestAnimationFrame(() => {
    // A slice grows with what is shown, up to OPEN_SLICE_MAX: every frame also lays out and lists again all that came before it.
    const slice = Math.min(OPEN_SLICE_MAX, Math.max(OPEN_SLICE, Math.floor(shown / 2)));
    shown = shown + slice >= nodes.value.length ? Infinity : shown + slice;
    feedNodes();
    feedEdges();
    if (shown !== Infinity) openNextSlice();
  });
}
watch(nodes, next => {
  if (!fed.length && shown === Infinity && next.length > OPEN_SLICE) { shown = OPEN_SLICE; openNextSlice(); }
  feedNodes();
}, { immediate: true });
watch(edges, feedEdges, { immediate: true });
onBeforeUnmount(() => cancelAnimationFrame(slicing));
const dragging = computed(() => local.value.size > 0);
/**
 * Only a very large board draws just what is on screen: every item that enters or leaves the view makes the browser lay out the whole
 * canvas again (about 80 ms per frame at 450 items), so panning and zooming stuttered. Below the limit all items stay in the page (BD2-2).
 * Measured again for 4.0: drawing only what is on screen from 300 items made zooming a board of 1430 items slower (45 against 56 frames
 * a second, frames of 180 ms) and its opening too, and Vue Flow keeps no margin around the view (cards would pop in while panning).
 */
const virtualise = computed(() => items.value.length > 1500);

const editTools = new Set<BoardTool>(["note", "text", "shape", "frame", "connector"]);
const activeTool = computed<BoardTool>(() => (props.canEdit || !editTools.has(tool.value) ? tool.value : "select"));
const panning = computed(() => activeTool.value === "hand" || keys.value.space);
// The middle button pans in every tool: one array, as a new one in each render of the canvas drew Vue Flow's viewport again.
const MIDDLE_BUTTON = [1];
// A finger pans and two zoom, in every tool (BD2-5): a touch has no middle button to pan with and no use for a selection rectangle. It
// starts from the device (a phone or tablet), and from the last pointer that touched the canvas where both a mouse and a screen exist.
const touching = ref(typeof matchMedia === "function" && matchMedia("(pointer: coarse)").matches);
// A touch has no Shift either (review 3.5.1, finding 10): with „Vybírat více“ on, a tap adds an item to the selection or takes it out,
// as Shift+click does. Vue Flow reads its multi-selection key only on a key press, so the switch sets the state the key would.
const multiTap = ref(false);
watch(() => multiTap.value && touching.value, on => { flow.multiSelectionActive.value = on; });

// Coordinates.
// Read once and kept: a pointer move or wheel tick asks again and again, and each read after a DOM change forces a layout of the whole
// canvas (BD2-2; a hover that marks the neighbours, 150 ms per 40 moves at 1× CPU). The canvas moves only with a resize or a scroll, or
// while the pointer is elsewhere, which drop the reading.
let rectRead: DOMRect | undefined;
const forgetRect = () => { rectRead = undefined; };
const rect = () => {
  if (!surface.value) return new DOMRect();
  return (rectRead ??= surface.value.getBoundingClientRect());
};
/** The canvas's size, for what is drawn in screen space (the timeline's labels). */
const size = ref({ width: 0, height: 0 });
// A board as narrow as a phone (U2-10): the rail lies along the bottom as a bar (its CSS, a container query) instead of
// covering a quarter of the width, the zoom moves to the top corner, and fitting leaves the bar's height free.
const COMPACT_WIDTH = 480, RAIL_BAR = 64, TITLE_ROOM = 28;
const compact = computed(() => size.value.width > 0 && size.value.width <= COMPACT_WIDTH);
let sizeObserver: ResizeObserver | undefined;
onMounted(() => {
  // A board that can be edited has its text editor and command panel in before they are wanted; one that only reads (the relations graph) never loads them.
  // „Co s tím jde dělat“ works while reading too (P10), so every board has its menu in before the first right click (P17).
  // At the latest after 2 s, so a busy board still has its text editor in before the first double-click (P17).
  whenIdle(() => void loadActionsMenu().catch(() => undefined));
  if (props.canEdit) whenIdle(() => { preheatTextEditor(); void loadCommandPanel().catch(() => undefined); });
  // The first pointer, key or wheel anywhere on the canvas (also its rail and zoom buttons) ends fitting it on a resize.
  const canvasElement = surface.value?.parentElement;
  for (const type of ["pointerdown", "keydown", "wheel"]) canvasElement?.addEventListener(type, () => { touched = true; }, { capture: true, passive: true });
  canvasElement?.addEventListener("pointerdown", event => { touching.value = event.pointerType === "touch"; pressed(event); }, { capture: true, passive: true });
  canvasElement?.addEventListener("pointermove", holdMove, { capture: true, passive: true });
  for (const type of ["pointerup", "pointercancel"]) canvasElement?.addEventListener(type, event => { holdEnd(); released(event as PointerEvent); }, { capture: true, passive: true });
  canvasElement?.addEventListener("pointerenter", forgetRect, { passive: true });
  window.addEventListener("resize", forgetRect, { passive: true });
  window.addEventListener("scroll", forgetRect, { capture: true, passive: true });
  if (!surface.value || typeof ResizeObserver === "undefined") return;
  sizeObserver = new ResizeObserver(([entry]) => {
    if (!entry) return;
    forgetRect();
    size.value = { width: entry.contentRect.width, height: entry.contentRect.height };
    fitOpened(true);
  });
  sizeObserver.observe(surface.value);
});
onBeforeUnmount(() => {
  sizeObserver?.disconnect(); clearTimeout(travelTimer); holdEnd();
  window.removeEventListener("resize", forgetRect);
  window.removeEventListener("scroll", forgetRect, { capture: true });
});
function toBoard(clientX: number, clientY: number): Point {
  const box = rect(), v = viewport.value;
  return { x: (clientX - box.left - v.x) / v.zoom, y: (clientY - box.top - v.y) / v.zoom };
}
function centre(): Point { const box = rect(); return toBoard(box.left + box.width / 2, box.top + box.height / 2); }
function visibleArea(): Geometry {
  const box = rect(), v = viewport.value;
  return { x: -v.x / v.zoom, y: -v.y / v.zoom, width: box.width / v.zoom, height: box.height / v.zoom };
}

// Emitting. A gesture of more than the server's 200 changes goes as several batches (and as several undo steps).
let ownChange = false;
/** Counts my gestures, undos and redos: a toast's „Zpět“ acts only while its gesture is still the last thing in my history. */
let gestures = 0;
// An undone item may be the one with the focus: the keys stay with the board (P7), not on the page body.
function undo() { gestures++; emit("undo"); void nextTick(keepKeys); }
function redo() { gestures++; emit("redo"); void nextTick(keepKeys); }
/** What my last gesture moved: its batch comes back as items after the local geometry is gone, and must not glide back. */
let settled: ReadonlySet<string> = new Set();
/** `moved`: what a drag or nudge put elsewhere, which joins or leaves the containers under it (boardGroups.ts). */
function send(changes: readonly BoardChange[], moved: readonly string[] = [], coalesce?: string) {
  if (!props.canEdit || !changes.length) return;
  ownChange = true;
  gestures++;
  // Every gesture keeps membership and the groups' and frames' bounds right, in its own batch.
  const arranged = arrangeChanges(items.value, changes, moved);
  // shortcut: a gesture over 200 changes is split into batches, so it takes several undo steps; give the model gesture groups if that bites.
  for (let start = 0; start < arranged.length; start += maxBatchChanges) emit("change", arranged.slice(start, start + maxBatchChanges), coalesce);
}
function select(ids: Iterable<string>) { selected.value = new Set(ids); }
// A host that shows something for the selection (the relations graph's dock) follows it.
watch(selected, ids => emit("selection", [...ids]));
// A string source: it fires when the card comes or the subject changes, not on every change of the items (a group opened keeps the reader's choice).
watch(() => (props.preselect && byId.value.has(props.preselect) ? props.preselect : null), id => { if (id) select([id]); }, { immediate: true, flush: "post" });
function focusSurface() { surface.value?.focus({ preventScroll: true }); }
/** Back from the context toolbar to what it acts on: the first selected item, else the board (BD-3, BD-4). */
function backToSelection() { const first = selectedIds()[0]; if (first) focusItem(first); else focusSurface(); }
/** After a button that may disable itself (Znovu with nothing more to redo) the board keeps the keys, not the page (BD-15); also after
 * a click on an item, as Vue Flow blurs a node or connector that Shift+click takes out of the selection (P7: never on <body>). */
// A focused button just disabled is still the active element in this frame; the browser moves the focus to <body> right after it.
function keepKeys() { requestAnimationFrame(() => { const active = document.activeElement; if (active === document.body || active?.matches(":disabled")) focusSurface(); }); }
/** Tab past the last item: on to what follows the board. The browser's own Tab would land on another item's node (BD-2). */
function leaveBoard() {
  const board = surface.value;
  if (!board) return;
  const stops = [...document.querySelectorAll<HTMLElement>("a[href], button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]")];
  stops.find(stop => stop.tabIndex >= 0 && !board.contains(stop) && stop.getClientRects().length > 0
    && !!(board.compareDocumentPosition(stop) & Node.DOCUMENT_POSITION_FOLLOWING))?.focus();
}
/** The element an item is drawn as: its node, a connector's edge. */
const itemElement = (id: string) => surface.value?.querySelector<HTMLElement>(`:is(.vue-flow__node, .vue-flow__edge)[data-id="${CSS.escape(id)}"]`);
/** Focuses the item's node or connector; one out of view is rendered only once the view reached it, so this tries a few frames. */
function focusItem(id: string, tries = 30) {
  // At once when the element is there (J12.1: a key right after Esc reaches the item), else a few frames later.
  const element = itemElement(id);
  element?.focus({ preventScroll: true });
  // A node Vue Flow has not measured yet is hidden and refuses the focus: again a frame later.
  if (element && document.activeElement === element) return;
  if (tries > 0) requestAnimationFrame(() => focusItem(id, tries - 1));
}

function presence() {
  if (!props.canEdit) return;
  const dragged = [...local.value].slice(0, 50).map(([itemId, g]) => ({ itemId, ...fitGeometry(g) }));
  const line = connectingNow();
  emit("preview", { ...(cursor && { cursor }), dragging: dragged, ...(line && { connecting: line }) });
}
/** The connector I am drawing, for the others (A8): its fixed end and side, and where its free end is (the item it would land at). */
function connectingNow(): PresencePayload["connecting"] {
  const state = drawing.value, link = linking.value;
  if (state && cursor) {
    const to = [...landing.value][0];
    return { from: state.fixed, ...(state.fixedSide && { side: state.fixedSide }), ...(to && { to }), x: cursor.x, y: cursor.y };
  }
  const target = link?.to ? byId.value.get(link.to) : undefined;
  return link && target ? { from: link.from, to: target.id, x: target.x + target.width / 2, y: target.y + target.height / 2 } : undefined;
}
/** The pointer in client coordinates while it is over the canvas: where the command panel opens. */
let pointer: Point | undefined;
/** When Space went down, while a short tap of it may still open the command panel. */
let spaceTap = 0;
function onPointerMove(event: PointerEvent) {
  keys.value.shift = event.shiftKey;
  keys.value.alt = event.altKey;
  cursor = toBoard(event.clientX, event.clientY);
  pointer = { x: event.clientX, y: event.clientY };
  // A connector being drawn shows the item it would attach to: the event's own target, the element under the pointer (nothing captures
  // the pointer while connecting), so a move needs no second hit test of the whole board.
  // A finger's moves all go to where it went down (implicit capture): its hit is the element under it.
  if (connecting.value) {
    const under = event.pointerType === "mouse" ? event.target as Element | null : document.elementFromPoint(event.clientX, event.clientY);
    const hit = under?.closest?.<HTMLElement>(".vue-flow__node")?.dataset.id;
    showDropTarget(hit && hit !== (connection?.from ?? reconnecting?.fixed) ? new Set([hit]) : new Set());
  }
  // Panning with Space held is no tap of it.
  if (keys.value.space && event.buttons) spaceTap = 0;
  presence();
}
// The objects of the cards on screen, for the view session's viewport (which throttles what it sends) on every pan, zoom and change.
watch(() => [viewport.value, items.value, size.value] as const, () => emit("viewport", visibleObjects(items.value, visibleArea())));
function onPointerLeave() { cursor = undefined; pointer = undefined; if (props.canEdit) emit("preview", { dragging: [] }); }

// Motion (spec §5): what entered, left or moved since the last items, decided by boardMotion.ts and played by the nodes.
let shownBefore: Map<string, Geometry> | null = null;
let itemsBefore = new Map<string, BoardViewItem>();
let motionKey = 0;
let motionTimer: ReturnType<typeof setTimeout> | undefined, leaveTimer: ReturnType<typeof setTimeout> | undefined;
// The stored items: a measured height is no movement to animate.
watch(() => props.items, items => {
  const shown = new Map(items.map(item => [item.id, geometryOf(item)]));
  const planned = planMotion(shownBefore, shown, { local: new Set([...local.value.keys(), ...settled]), reduced: prefersReducedMotion(surface.value), own: ownChange });
  // What happened to a group shows instead (boardMotion.ts): its outline drawing in, dissolving or refitting, members tucked away.
  // A group's outline is drawn around what is on screen: a component that flows by its measured height.
  const drawn = new Map(items.map(item => [item.id, byId.value.get(item.id) ?? item]));
  for (const [id, step] of planGroupMotion(shownBefore && itemsBefore, drawn)) planned.set(id, step);
  ownChange = false;
  settled = new Set();
  if (planned.size) {
    const next = new Map(motion.value), gone = new Map(leaving.value);
    for (const [id, step] of planned) {
      if (step.kind === "leave") { const item = itemsBefore.get(id); if (item) gone.set(id, item); else continue; }
      next.set(id, { ...step, key: ++motionKey, ...(step.kind === "glide" && { from: shownBefore!.get(id), to: shown.get(id) }) });
    }
    for (const id of shown.keys()) gone.delete(id);
    motion.value = next;
    leaving.value = gone;
    const el = surface.value ?? document.body;
    clearTimeout(motionTimer);
    motionTimer = setTimeout(() => { motion.value = new Map([...motion.value].filter(([id]) => leaving.value.has(id))); }, motionMs(el, "--atlas-motion-slow") + literalMs(el, 40 * 8));
    if (gone.size) { clearTimeout(leaveTimer); leaveTimer = setTimeout(() => { leaving.value = new Map(); motion.value = new Map([...motion.value].filter(([id]) => !gone.has(id))); }, motionMs(el, "--atlas-motion-surface")); }
  }
  // Forget the selection of what is gone (deleted by someone else, undone).
  if ([...selected.value].some(id => !shown.has(id))) select([...selected.value].filter(id => shown.has(id)));
  if (editing.value && !shown.has(editing.value)) { loseEditing(editing.value); editing.value = undefined; }
  shownBefore = shown;
  itemsBefore = drawn;
// Synchronous, so that the leaving items are in place before the nodes are given to Vue Flow: a node it dropped for a moment
// would come back as a new object under the old, reused wrapper.
}, { immediate: true, flush: "sync" });

// Vue Flow's changes: selection is ours, positions of a drag become local geometry, everything else is measured by Vue Flow. Additions
// are the ones `feed` asked for (addNodes, addEdges): applied as they come.
interface Drag { lead: string; ids: string[]; origin: Map<string, Geometry>; raw?: Point; others: Geometry[]; grid?: GridSnap; targets: (local: ReadonlyMap<string, Geometry>) => Set<string> }
/** The board's grid for snapping (spec §3.7): its step, on a timeline the divisions of the level in view. */
function gridSnap(): GridSnap | undefined {
  const { snapToGrid, gridSize, background, timeline } = props.settings;
  if (!snapToGrid) return undefined;
  const view = visibleArea();
  return { step: gridSize, ...(background === "timeline" && timeline && { xTicks: timelineTicks(timeline, viewport.value.zoom, { x: view.x - view.width, width: view.width * 3 }).ticks.map(tick => tick.x) }) };
}
let drag: Drag | undefined;
function onNodesChange(changes: NodeChange[]) {
  const added = changes.filter(change => change.type === "add");
  if (added.length) flow.applyNodeChanges(added);
  let next: Set<string> | undefined;
  const heights = new Map<string, number>();
  for (const change of changes) {
    if (change.type === "select") { next ??= new Set(selected.value); if (change.selected) next.add(change.id); else next.delete(change.id); }
    else if (change.type === "position" && change.dragging && change.position && drag && change.id === drag.lead) drag.raw = change.position;
    else if (change.type === "dimensions" && change.dimensions && !change.resizing) heights.set(change.id, change.dimensions.height);
  }
  if (next) selected.value = next;
  if (drag?.raw) followDrag();
  noteHeights(heights);
}
const nodeElement = (id: string) => surface.value?.querySelector<HTMLElement>(`.vue-flow__node[data-id="${CSS.escape(id)}"]`);
/** The height a component's node has on screen now; 0 for a preview tile (no content) or none laid out. */
function shownHeight(id: string) {
  const element = nodeElement(id);
  return element && !element.querySelector("[data-board-block-preview]") ? element.offsetHeight : 0;
}
function noteHeights(heights: ReadonlyMap<string, number>) {
  if (!heights.size) return;
  const stored = new Map(props.items.map(item => [item.id, item]));
  const changed = [...heights].filter(([id, height]) => {
    const item = stored.get(id);
    return item && blockLayoutOf(item) === "flow" && height > 0 && measured.value.get(id) !== height;
  });
  // A preview tile is no content: it stands at the stored height plus its frame, which would grow the item on its next move. One read
  // for all of them (a query per node walked every node of the board for each).
  const previewed = changed.length ? new Set([...surface.value?.querySelectorAll("[data-board-block-preview]") ?? []].map(tile => tile.closest<HTMLElement>(".vue-flow__node")?.dataset.id)) : undefined;
  const flowing = changed.filter(([id]) => !previewed?.has(id));
  // Only what is still on the board is kept: a deleted item's height goes with it.
  if (flowing.length) measured.value = new Map([...[...measured.value].filter(([id]) => stored.has(id)), ...flowing]);
}
function onEdgesChange(changes: EdgeChange[]) {
  const added = changes.filter(change => change.type === "add");
  if (added.length) flow.applyEdgeChanges(added);
  let next: Set<string> | undefined;
  const marquee = flow.userSelectionActive.value;
  for (const change of changes) {
    if (change.type !== "select") continue;
    next ??= new Set(selected.value);
    const connector = byId.value.get(change.id);
    // A marquee takes a connector only with both its ends, not every connector that touches what it took.
    const wanted = change.selected && (!marquee || (!!connector && next.has(connector.from ?? "") && next.has(connector.to ?? "")));
    if (wanted) next.add(change.id); else next.delete(change.id);
  }
  if (next) selected.value = next;
}

function onDragStart({ nodes: dragged }: { nodes: GraphNode[] }) {
  if (!props.canEdit || !dragged.length) return;
  commitNudge();
  const ids = withMembers(dragged.map(node => node.id), items.value);
  const origin = new Map(ids.flatMap(id => { const item = byId.value.get(id); return item ? [[id, geometryOf(item)] as const] : []; }));
  // Neighbours to align with: what is in view and does not move along.
  const view = visibleArea();
  const others = cards.value.filter(item => !origin.has(item.id) && item.x < view.x + view.width && item.x + item.width > view.x && item.y < view.y + view.height && item.y + item.height > view.y).map(geometryOf);
  drag = { lead: dragged[0]!.id, ids, origin, others, grid: gridSnap(), targets: dropTargetsOf(items.value, ids) };
  dragGeometry = new Map(origin);
  dragActive.value = true;
}
function followDrag() {
  const current = drag!, start = current.origin.get(current.lead)!;
  let dx = current.raw!.x - start.x, dy = current.raw!.y - start.y;
  if (!keys.value.alt) {
    const moving = boundsOf([...current.origin.values()].map(g => ({ ...g, x: g.x + dx, y: g.y + dy })));
    const snap = snapMove(moving, current.others, 6 / viewport.value.zoom, current.grid);
    dx += snap.dx;
    dy += snap.dy;
    guides.value = snap.guides;
  } else guides.value = [];
  follow(new Map([...current.origin].map(([id, g]) => [id, { ...g, x: g.x + dx, y: g.y + dy }])));
  showDropTarget(current.targets(dragGeometry));
}
/** One frame of a drag or resize: the moved nodes take the geometry in the store at once, the rest is not touched. */
function follow(next: ReadonlyMap<string, Geometry>) {
  dragActive.value = true;
  dragGeometry = next;
  local.value = next;
  for (const [id, g] of next) {
    const graph = flow.findNode(id);
    if (!graph) continue;
    graph.position = { x: g.x, y: g.y };
    graph.width = g.width;
    // A component that flows keeps the height of its content while it is moved or made wider.
    const item = byId.value.get(id);
    const flowing = !!item && blockLayoutOf(item) === "flow";
    if (!flowing) graph.height = g.height;
    // Vue Flow's getStyle writes the size into the node's style object once and never refreshes it (`!styles.width && width`), so a
    // frame later in the gesture would keep the first one's size until the nodes are derived anew on release: the style takes it too.
    graph.style = { ...(graph.style as object), width: `${g.width}px`, height: flowing ? undefined : `${g.height}px` };
  }
  presence();
}
function onDragStop() {
  if (!drag) return;
  drag = undefined;
  dragActive.value = false;
  guides.value = [];
  showDropTarget(new Set());
  finishLocal();
}
/** Sends my local geometry as one batch and lets go of it: the items now carry it (or, read-only, the item goes back). */
function finishLocal() {
  // Against what the board stores: a component that flows takes its height along as its content lays out now (a width changed by
  // Alt with the arrows or a handle reflowed it after the series began), read from its node, else as last measured.
  const placed = new Map([...local.value].map(([id, g]) => {
    const item = byId.value.get(id), height = item && blockLayoutOf(item) === "flow" ? shownHeight(id) || measured.value.get(id) : undefined;
    return [id, height ? { ...g, height } : g] as const;
  }));
  const changes = moveChanges(props.items, placed);
  settled = new Set(local.value.keys());
  send(changes, [...local.value.keys()]);
  local.value = new Map();
  presence();
}

// Resize from a node.
function resizing(id: string, g: Geometry) {
  const grid = keys.value.alt ? undefined : gridSnap();
  const snapped = grid ? snapResize(g, grid.step) : g;
  // A component that flows is as tall as its content, never a grid step.
  const item = byId.value.get(id);
  follow(new Map([[id, item && blockLayoutOf(item) === "flow" ? { ...snapped, height: g.height } : snapped]]));
}
function resized() { dragActive.value = false; finishLocal(); }

// Arrow nudges: 1 px, Shift 10 px; a series within 600 ms is one batch and one undo step (spec §3.2).
let nudge: { kind: "move" | "resize"; ids: string[]; timer: ReturnType<typeof setTimeout> } | undefined;
function nudgeBy(dx: number, dy: number) {
  const chosen = selectedItems.value.filter(item => item.kind !== "connector").map(item => item.id);
  if (!props.canEdit || !chosen.length) return;
  // A series is one gesture: a resize under way is sent before a move starts (and the other way round).
  const sent = nudge?.kind === "resize" ? commitNudge() : undefined;
  nudge ??= { kind: "move", ids: withMembers(chosen, items.value), timer: undefined as never };
  clearTimeout(nudge.timer);
  local.value = new Map(nudge.ids.flatMap(id => {
    const g = local.value.get(id) ?? sent?.get(id) ?? (byId.value.get(id) && geometryOf(byId.value.get(id)!));
    return g ? [[id, { ...g, x: g.x + dx, y: g.y + dy }] as const] : [];
  }));
  showDropTarget(dropTargets(items.value, local.value));
  presence();
  nudge.timer = setTimeout(commitNudge, 600);
}
/** Alt with the arrows (keyboard-first, no mouse needed): the one selected item grows or shrinks from its right and bottom edge, one
 * batch and one undo step per series as a nudge; a component that flows takes the width only, its content decides its height. */
function resizeBy(dw: number, dh: number) {
  const item = single.value ? byId.value.get(single.value) : undefined;
  if (!props.canEdit) return;
  // What cannot be resized says why, not nothing.
  if (!item) { say(t.canvas.sizeOne); return; }
  if (item.kind === "connector" || item.kind === "group") { say(item.kind === "group" ? t.canvas.groupSize : t.canvas.connectorSize); return; }
  const flowing = blockLayoutOf(item) === "flow";
  if (flowing && !dw) { say(t.canvas.flowHeight); return; }
  const sent = nudge && (nudge.kind !== "resize" || nudge.ids[0] !== item.id) ? commitNudge() : undefined;
  nudge ??= { kind: "resize", ids: [item.id], timer: undefined as never };
  clearTimeout(nudge.timer);
  const g = local.value.get(item.id) ?? sent?.get(item.id) ?? geometryOf(item);
  const min = minSizeOf(item);
  const next = fitGeometry({ ...g, width: Math.max(min.width, g.width + dw), height: flowing ? g.height : Math.max(min.height, g.height + dh) });
  local.value = new Map([[item.id, next]]);
  // Said, as a nudge's place is seen: the new size, a component that flows by its width alone; at a limit, that it is one.
  const size = flowing ? t.canvas.width(Math.round(next.width)) : t.canvas.widthHeight(Math.round(next.width), Math.round(next.height));
  say(next.width === g.width && next.height === g.height ? t.canvas.sizeLimit(size) : size);
  presence();
  nudge.timer = setTimeout(commitNudge, 600);
}
/** Sends the series under way; returns its geometry, which the board's items take only after this tick (a series of the other kind
 * started in the same key goes on from it, not from where the items were). */
function commitNudge() {
  if (!nudge) return undefined;
  const sent = local.value;
  clearTimeout(nudge.timer);
  nudge = undefined;
  showDropTarget(new Set());
  finishLocal();
  return sent;
}

// Creating.
const textKinds = new Set(["note", "text", "shape", "frame", "group"]);
/** The note just created for typing, while nothing came after it: left empty, it goes with the end of its editing (the creation is undone). */
let fresh: { id: string; step: number } | undefined;
function create(kind: "note" | "text" | "shape" | "frame", at: Point) {
  const id = newId();
  send([createChange(kind, at, { id, z: topZ(items.value) })]);
  if (kind === "note" && props.canEdit) fresh = { id, step: gestures };
  select([id]);
  if (props.canEdit) { editing.value = id; awaitingEditor = true; awaitingSince = performance.now(); }
  tool.value = "select";
}
function insert(inserted: readonly BoardInsert[], at: Point = cursor ?? centre()) {
  if (!inserted.length) return;
  if (placedContacts(inserted, items.value).length) toaster.warning(repeatedContact());
  let { changes, ids } = insertChanges(inserted, at, newId, topZ(items.value));
  const [first] = changes;
  if (changes.length === 1 && first?.type === "create") changes = [{ ...first, item: { ...first.item, ...freeSpot(first.item, cards.value, visibleArea()) } }];
  send(changes);
  select(ids);
}
async function pickPages() {
  const picked = props.canEdit ? await props.pickPage?.() : null;
  if (!picked) return;
  const pages = (Array.isArray(picked) ? picked : [picked]) as PickedPage[];
  insert(pages.map(page => ({ kind: "page", resourceId: page.id, ...(page.anchor && { anchor: page.anchor }) })), centre());
}
async function pickBlocks(pick: (() => Promise<readonly BoardBlockNode[] | null>) | undefined) {
  const blocks = props.canEdit && pick ? await pick() : null;
  if (blocks?.length) insert(blocks.map(block => ({ kind: "block", block })), centre());
}
const pickIssues = () => pickBlocks(props.pickIssues);
async function pickContacts() {
  const picked = props.canEdit && props.pickContacts ? await props.pickContacts() : null;
  if (picked?.length) insert(picked.map(object => ({ kind: "object", ...object })), centre());
}
/** Pasted or dropped items: each YouTrack reference becomes the issue's editor block, or stays a note when it does not resolve. */
async function insertPasted(items: readonly PasteItem[], at: Point) {
  if (!items.length) return;
  insert(await resolvePasteItems(items, props.resolveIssue ?? (async () => null)), at);
}
// „Komponenta“ (K): the page editor's blocks (editorBlocks.ts, read once when the menu is first wanted); I and C go to the pickers,
// which stand in for the plugins' own entries for the same blocks (one entry per use case).
const blockModules = shallowRef<readonly EditorBlockModule[]>([]);
// The editor's registry loads with the first wanted menu, not with a canvas that only reads (the relations graph): `editorBlocks`.
async function loadBlockModules() {
  if (!props.canEdit || blockModules.value.length) return;
  const { useEditorBlockModules } = await editorBlocks();
  // No „Moduly produktu“ on a board: a catalog pick is requirement headings with their lines, which a board would split into items (a
  // decision on requirements on boards is pending); the plain „Požadavek“ is one heading.
  blockModules.value = useEditorBlockModules();
}
/** A block whose plugin inserts by its own picker (YouTrack): its editor runs empty over the board until it inserts or is left. */
const inserting = shallowRef<{ module: EditorBlockModule; at: Point } | null>(null);
async function onComponent(id: string) {
  if (id === "pick:youtrack") { void pickIssues(); return; }
  if (id === "pick:contact") { void pickContacts(); return; }
  if (id.startsWith("links:")) { addLinks(id.slice(6) as BoardLinksShow); return; }
  const module = blockModules.value.find(entry => entry.id === id);
  if (!module) return;
  const node = (await editorBlocks()).defaultBlockNode(module);
  if (!node) { inserting.value = { module, at: centre() }; return; }
  // The plugin's defaults, and its own editing opens at once (spec §3.6).
  const blockId = newId();
  const change = createChange("block", centre(), { id: blockId, z: topZ(items.value), props: { block: node } });
  send([change.type === "create" ? { ...change, item: { ...change.item, ...freeSpot(change.item, cards.value, visibleArea()) } } : change]);
  select([blockId]);
  editing.value = blockId;
}
/** A list derived from the relations (P15) where it hides nothing; „Kde se používá“ asks at once what to follow (its editing). */
function addLinks(show: BoardLinksShow) {
  const id = newId();
  const change = createChange("links", centre(), { id, z: topZ(items.value), props: { show } });
  send([change.type === "create" ? { ...change, item: { ...change.item, ...freeSpot(change.item, cards.value, visibleArea()) } } : change]);
  select([id]);
  if (show === "usage") editing.value = id;
}
/** What an insertion made: its block, and the others it brought (a catalog requirement and what it requires). */
function inserted(node: BoardBlockNode, others: readonly BoardBlockNode[] = []) {
  const at = inserting.value?.at ?? centre();
  inserting.value = null;
  insert([node, ...others].map(block => ({ kind: "block" as const, block })), at);
}
const saveBlock = (id: string, node: BoardBlockNode) => send(styleChanges(items.value, [id], { props: { block: node } }), [], editingRun(id));
function commitBlock(id: string, node: BoardBlockNode) {
  saveBlock(id, node);
  endEdit(id);
}
function removeItem(id: string) {
  void contactsLeft([id]);
  send(deleteChanges([id]));
  if (editing.value === id) editing.value = undefined;
  select([...selected.value].filter(other => other !== id));
}

// Pane: a click with a creation tool places the item, a double-click places a note, a click elsewhere ends what was going on.
function onPaneClick(event: MouseEvent) {
  if (linking.value) stopLinking();
  focusSurface();
  const at = toBoard(event.clientX, event.clientY);
  const kind = activeTool.value;
  if (kind === "note" || kind === "text" || kind === "shape" || kind === "frame") { create(kind, at); return; }
  // The frame rule (owner, 27. 10.; FigJam's): a click on the empty inside of a frame or group, or on its outline, selects it; a drag on the
  // empty inside draws a marquee unless it is selected (then it moves it with everything in it, BoardNode). A drag on the outline (the
  // ring that always takes the pointer) and on the name moves it whether selected or not (BD2-6; the owner decides whether the outline should draw).
  if (activeTool.value !== "select") return;
  const container = containerAt(items.value, at, 6 / viewport.value.zoom);
  if (container) select([container]);
}
function onDoubleClick(event: MouseEvent) {
  if (!props.canEdit || !(event.target as Element).classList?.contains("vue-flow__pane")) return;
  create("note", toBoard(event.clientX, event.clientY));
}
function onNodeDoubleClick({ node, event }: { node: GraphNode; event?: MouseEvent | TouchEvent }) {
  // A double-click on the empty inside of a selected frame adds a note there, as on the board; its title still renames it.
  const point = event && ("clientX" in event ? event : event.changedTouches[0]);
  if (props.canEdit && point && (event.target as Element | null)?.closest?.(".board-frame-body, .board-group-body")) { create("note", toBoard(point.clientX, point.clientY)); return; }
  startEdit(node.id, point && { x: point.clientX, y: point.clientY, word: true });
}
/** The press of a click: on the text of the item that was already the one selected (contract V3: the second click writes there). */
let press: { id: string; x: number; y: number } | undefined;
const entering = ref<string>();
function pressed(event: PointerEvent) {
  const target = event.target as Element | null;
  const id = single.value;
  press = id && event.button === 0 && !target?.closest?.("a, button, [role=button]") && target?.closest?.(".ProseMirror") && itemElement(id)?.contains(target)
    ? { id, x: event.clientX, y: event.clientY } : undefined;
  // The text's editor is built while the button is down (a click lasts about 100 ms), so the release edits at once. Only after the
  // press's own events: an element swapped under them would take the mousedown away from the node, and with it a drag that starts there.
  if (press && props.canEdit) setTimeout(() => { entering.value = id; });
  holdStart(event);
}
/** Its release where it was pressed starts the editing there. Not the click: the editor built under the press takes the click away. */
function released(event: PointerEvent) {
  const at = press;
  press = undefined;
  const tool = activeTool.value;
  // A creation tool places its item on top of the card (onNodeClick), Shift, Ctrl and „Vybírat více“ change the selection, a press that
  // moved dragged the card.
  if (!at || event.type !== "pointerup" || tool === "note" || tool === "text" || tool === "shape" || tool === "frame") return;
  if (event.shiftKey || event.ctrlKey || event.metaKey || (multiTap.value && touching.value)) return;
  if (editing.value !== at.id && Math.hypot(event.clientX - at.x, event.clientY - at.y) < 4) startEdit(at.id, { x: event.clientX, y: event.clientY });
}
/**
 * A long press of a finger on an item opens its actions (contract J2.6), as the right button does: Android sends the menu's own event
 * then, iOS does not, so the press says it itself after a while held still. Not in a text being written, which selects its words.
 */
let hold: { timer: ReturnType<typeof setTimeout>; x: number; y: number } | undefined;
const HOLD_MS = 550, HOLD_SLOP = 8;
function holdStart(event: PointerEvent) {
  holdEnd();
  const target = event.target as Element | null;
  if (event.pointerType !== "touch" || !target?.closest?.(".vue-flow__node, .vue-flow__edge, [data-board-edge-label]") || typing(target)) return;
  const at = { x: event.clientX, y: event.clientY };
  hold = { ...at, timer: setTimeout(() => {
    hold = undefined;
    if (target.isConnected) target.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: at.x, clientY: at.y }));
  }, HOLD_MS) };
}
function holdMove(event: PointerEvent) { if (hold && Math.hypot(event.clientX - hold.x, event.clientY - hold.y) > HOLD_SLOP) holdEnd(); }
function holdEnd() { if (hold) clearTimeout(hold.timer); hold = undefined; }
function onNodeClick({ node, event }: { node: GraphNode; event?: MouseEvent | TouchEvent }) {
  const point = event && ("clientX" in event ? event : event.changedTouches[0]);
  // A creation tool places its item on top of a card as on the empty board (a click never only selects then, and what is typed next goes in).
  const tool = activeTool.value;
  if (props.canEdit && point && (tool === "note" || tool === "text" || tool === "shape" || tool === "frame")) { create(tool, toBoard(point.clientX, point.clientY)); return; }
  // The second click into the selected card's text started its editing as it was released (`released`).
  if (editing.value === node.id) return;
  keepKeys();
}
function onEdgeDoubleClick({ edge }: { edge: GraphEdge }) { startEdit(edge.id); }

// Text.
/**
 * The page a page card („Karta“) edits, in the page's editor over the board (COL2-9), not a requirement's card. The panel itself is
 * read-only for a page this person may only read: a card's right to edit may not be known yet (a page just made from a note).
 */
function pageToEdit(item: BoardViewItem) {
  if (item.kind !== "page") return undefined;
  const view = readPageTarget(item.target, item.targetResourceId);
  return view?.available && !view.typeId ? view : undefined;
}
/** The item is edited where it is (a text, a title or label, a component, a contact's fields); a board card has nothing to edit. */
const editsInPlace = (item: BoardViewItem) => props.canEdit && !isBoardCard(item) && (textKinds.has(item.kind) || item.kind === "connector" || item.kind === "block" || item.kind === "object" || item.kind === "links");
/** Enter, F2 and a double-click (D1): the item's editing, never its opening, which is „Otevřít“ (Ctrl+Enter, the menu, the card's link). */
const editPoint = ref<EditPoint>();
function startEdit(id: string, at?: EditPoint) {
  const item = byId.value.get(id);
  if (!item) return;
  // A reader (or the lock) edits nothing, the page over the board neither: the gesture reads the card instead (J1.3, K13).
  if (!props.canEdit) { if (opensAs(item)) open(item); return; }
  // A page's preview („Náhled“) is edited where its text is, as a board text (3.6, no „Upravit“); PagePreview asks the page's own right.
  const preview = item.kind === "page" && item.props?.display === "preview" && !!item.targetResourceId;
  const page = preview ? undefined : pageToEdit(item);
  if (page) { editPage(page.resourceId, page.title); return; }
  if (!preview && !editsInPlace(item)) return;
  select([id]);
  editPoint.value = at;
  editing.value = id;
  awaitingEditor = true;
  awaitingSince = performance.now();
  editRun++;
}
/** Which editing of an item a save belongs to: one undo step per editing, however many pauses it saved at (W4-4). */
let editRun = 0;
const editingRun = (id: string) => (editing.value === id ? `${id}#${editRun}` : undefined);
// The editor commits also when it goes away mid-edit (another item edited, the board closed): its text is saved all the same.
function saveText(id: string, text: string) {
  const item = byId.value.get(id);
  if (item) send(textChanges(item, text), [], editingRun(id));
  else if (lost?.id === id) keepLostText(lost, text);
}
/**
 * The item being typed into was removed by someone else. Its editor ends at once; what was typed is kept as the text's draft (the next
 * editing of the item, once it is restored, starts from it) and said with a way back (`keepLostText`), and the keys still arriving
 * for it belong to no shortcut of the board (`holdKeys`).
 */
let lost: BoardViewItem | undefined;
let lostTyping = 0;
function loseEditing(id: string) {
  const item = itemsBefore.get(id);
  if (!item || !(textKinds.has(item.kind) || item.kind === "connector")) return;
  lost = item;
  if (itemElement(id)?.contains(document.activeElement)) { lostTyping = performance.now() + 800; void nextTick(focusSurface); }
}
function keepLostText(item: BoardViewItem, text: string) {
  lost = undefined;
  if (text === (item.text ?? "")) return;
  const key = `board-text:${item.id}`;
  drafts.set(key, text);
  toaster.success(t.canvas.lostText, {
    action: { label: t.canvas.restoreWithText, run: () => { drafts.delete(key); send([{ type: "restore", itemId: item.id }, ...textChanges(item, text)]); } },
  });
}
function commitText(id: string, text: string) {
  saveText(id, text);
  endEdit(id);
}
/** A card's object edited in place (a contact's field), already on its way through the object path: one step of the board's undo,
 * all saves of one editing together (P6: one undo stack per board). Never in a batch to the server (boardModel). */
function editedObject(id: string, edit: Pick<ObjectEdit, "objectId" | "before" | "after">) {
  if (!props.canEdit) return;
  gestures++;
  emit("change", [{ type: "object", itemId: id, ...edit }], editingRun(id));
}
/** The items whose editor holds typed text not yet saved (U3-1): the board says „Neuloženo“ meanwhile. */
const holding = new Set<string>();
function unsaved(id: string, now: boolean) {
  const before = holding.size > 0;
  if (now) holding.add(id); else holding.delete(id);
  if (before !== holding.size > 0) emit("unsaved", holding.size > 0);
}
function endEdit(id: string) {
  if (editing.value !== id) return;
  // Keys typed for an editor that never took them (the editing ended first): they are the item's text all the same, never lost. A page's
  // preview has no text of its own on the board: an Esc before the page's editor came cancels them with the entering.
  if (typedAhead) { const item = byId.value.get(id); if (item && item.kind !== "page") saveText(id, (item.text ?? "") + typedAhead); }
  const untouched = fresh?.id === id && fresh.step === gestures && !byId.value.get(id)?.text;
  fresh = undefined;
  editing.value = undefined;
  typedAhead = "";
  awaitingEditor = false;
  endAfterKeys = false;
  if (untouched) { undo(); focusSurface(); return; }
  focusItem(id);
}
// The right to write went mid-edit (a lock, a lost right, the board closed): the editing ends at once and its editor turns read-only in
// place; what was typed waits as the text's draft for the next editing (BoardTextEditor), said once here (plan D10).
watch(() => props.canEdit, can => {
  const id = editing.value;
  if (can || !id) return;
  endEdit(id);
  toaster.warning(t.canvas.cannotEdit);
});
function openTarget(id: string, typeId: string) {
  if (typeId === "atlas.page") emit("openPage", id);
  else emit("openObject", id, typeId);
}
/** What a derived list follows (its page or object), readable and known by its type; none for the whole space. */
const followed = (item: BoardViewItem) => {
  const object = item.kind === "links" && item.props?.objectId ? liveObjects.get(item.props.objectId) : undefined;
  return object?.available ? object : undefined;
};
function open(item: BoardViewItem) {
  const object = followed(item);
  if (object) { openTarget(object.id, object.typeId); return; }
  if (item.kind === "page" && item.targetResourceId) emit("openPage", item.targetResourceId, item.props?.anchor);
  else if (isBoardCard(item)) emit("openObject", item.targetResourceId!, "atlas.board");
}
/** A card's object type when it stands for no page (a board or a contact in the relations graph, which draws every node as a page card). */
const cardType = (item: BoardViewItem) => (isBoardCard(item) ? "atlas.board" : item.kind === "page" ? readPageTarget(item.target, item.targetResourceId)?.typeId : undefined);
const opensWords: Record<string, string> = t.canvas.opens;
/** The words of „Otevřít“ for the item (Ctrl+Enter, the menu), none when it opens nothing. A module's object opens by its host. */
function opensAs(item: BoardViewItem) {
  if (item.kind === "links") { const object = followed(item); return object && (opensWords[object.typeId] ?? t.canvas.open); }
  if (!item.targetResourceId) return undefined;
  if (item.kind !== "page" && !isBoardCard(item)) return undefined;
  const type = cardType(item);
  // The relations graph's stand-in for a link that leads nowhere (objects/relationGraph.ts UNAVAILABLE_ID) opens nothing.
  if (readPageTarget(item.target, item.targetResourceId)?.available === false || type === "unavailable") return undefined;
  return opensWords[type ?? "atlas.page"] ?? t.canvas.open;
}
/** The address of what a card stands for, for „Kopírovat odkaz“: a page's or a board's (a contact opens in the dock, a module's object has none here). */
function addressOf(item: BoardViewItem) {
  const type = cardType(item);
  if (!item.targetResourceId || !opensAs(item) || (type && type !== "atlas.page" && type !== "atlas.board")) return undefined;
  if (type === "atlas.board") return boardLink(item.targetResourceId);
  return pageLink(item.targetResourceId, item.props?.anchor);
}
async function copyLink(item: BoardViewItem) {
  const href = addressOf(item);
  if (!href) return;
  try { await navigator.clipboard.writeText(new URL(href, location.href).href); toaster.success(t.canvas.linkCopied); }
  catch { toaster.error(t.canvas.linkNotCopied); }
}

// Connecting (spec §3.3): from a handle to another item's nearest side, or to a new note dropped on empty board.
let connection: { from: string; side?: BoardSide; done: boolean } | undefined;
let reconnecting: { edge: string; end: "source" | "target"; fixed?: string; done: boolean } | undefined;
const handleSide = (handle: string | null | undefined) => (handle && handle !== "auto" ? (handle as BoardSide) : undefined);
/** The connector being drawn or one of its ends being moved (A3): its look, its end that stays and that end's side (unset: facing
 * the other end), which end moves, and whether it lands with facing sides (the connector tool). */
interface Drawing { look: ConnectorStyle; fixed: string; fixedSide?: BoardSide; moving: "source" | "target"; auto: boolean }
const drawing = shallowRef<Drawing | null>(null);
/** A connector's look and the side of its end that stays while the other one moves. */
function drawingOf(connector: BoardItem, moving: "source" | "target"): Drawing {
  const look = propsOf(connector);
  return { look: { ...look, ...(connector.color && { color: connector.color }) }, fixed: (moving === "target" ? connector.from : connector.to)!,
    fixedSide: moving === "target" ? look.fromSide : look.toSide, moving, auto: false };
}
function onConnectStart({ nodeId, handleId }: { nodeId?: string | null; handleId?: string | null }) {
  // Handles stay connectable on a board read only (switching the lock changes nothing on the cards): they take no pointer then.
  if (!props.canEdit || !nodeId) return;
  // Moving an end shows the cards' handles and the card it would land at as drawing a new connector does (A2).
  connecting.value = true;
  if (reconnecting) return;
  connection = { from: nodeId, side: handleSide(handleId), done: false };
  drawing.value = { look: { ...defaultProps("connector"), ...connectorStyle }, fixed: nodeId, fixedSide: connection.side, moving: "target", auto: activeTool.value === "connector" };
}
function onConnect(made: Connection) {
  if (!connection || reconnecting) return;
  connection.done = true;
  send(connectChanges(items.value, { id: newId(), from: made.source, to: made.target, fromSide: handleSide(made.sourceHandle), toSide: handleSide(made.targetHandle), style: connectorStyle }));
}
/** The item under a pointer that let go, and the board point. */
function dropTarget(event?: MouseEvent | TouchEvent) {
  const point = event && ("changedTouches" in event ? event.changedTouches[0] : event);
  if (!point) return undefined;
  const hit = document.elementFromPoint(point.clientX, point.clientY)?.closest<HTMLElement>(".vue-flow__node");
  const item = hit?.dataset.id ? byId.value.get(hit.dataset.id) : undefined;
  return { item: item?.kind === "connector" ? undefined : item, at: toBoard(point.clientX, point.clientY), pane: !!document.elementFromPoint(point.clientX, point.clientY)?.closest(".vue-flow__pane, .vue-flow__selection") };
}
function onConnectEnd(event?: MouseEvent | TouchEvent) {
  connecting.value = false;
  drawing.value = null;
  showDropTarget(new Set());
  presence();
  const started = connection;
  connection = undefined;
  if (!started || started.done || reconnecting) return;
  const target = dropTarget(event);
  if (!target) return;
  const auto = activeTool.value === "connector";
  if (target.item && target.item.id !== started.from) {
    send(connectChanges(items.value, { id: newId(), from: started.from, to: target.item.id, fromSide: started.side, toSide: auto ? undefined : nearestSide(target.item, target.at), style: connectorStyle }));
  } else if (!target.item && target.pane) {
    const noteId = newId();
    send(connectToNewNoteChanges(items.value, { noteId, connectorId: newId(), from: started.from, fromSide: started.side, at: target.at, z: topZ(items.value), style: connectorStyle }));
    select([noteId]);
    editing.value = noteId;
  }
}
function onEdgeUpdateStart({ event, edge }: { event: MouseEvent | TouchEvent; edge: GraphEdge }) {
  // The ends stay mounted on a board read only (the lock switches nothing on every connector, J12.10), hidden and refused here.
  const connector = byId.value.get(edge.id);
  if (!props.canEdit || !connector) return;
  const end = (event.target as Element | null)?.closest("[data-type]")?.getAttribute("data-type") === "source" ? "source" : "target";
  drawing.value = drawingOf(connector, end);
  reconnecting = { edge: edge.id, end, fixed: drawing.value.fixed, done: false };
}
/** The connector's `end` moved to `to` at `side`: the same connector (A4), its other end and side kept, and selected. */
function reconnect(connector: BoardItem, end: "source" | "target", to: string, side: BoardSide | undefined) {
  const current = connector.props ?? {};
  send(reconnectChanges(connector, end === "target"
    ? { from: connector.from!, to, fromSide: current.fromSide, toSide: side }
    : { from: to, to: connector.to!, fromSide: side, toSide: current.toSide }));
  select([connector.id]);
}
function onEdgeUpdate({ edge, connection: next }: { edge: GraphEdge; connection: Connection }) {
  const connector = byId.value.get(edge.id);
  if (!connector || !reconnecting) return;
  reconnecting.done = true;
  const end = reconnecting.end;
  reconnect(connector, end, end === "target" ? next.target : next.source, handleSide(end === "target" ? next.targetHandle : next.sourceHandle));
}
function onEdgeUpdateEnd({ event, edge }: { event: MouseEvent | TouchEvent; edge: GraphEdge }) {
  const state = reconnecting;
  reconnecting = undefined;
  drawing.value = null;
  const connector = byId.value.get(edge.id);
  if (!state || state.done || !connector) return;
  const target = dropTarget(event);
  if (target?.item) reconnect(connector, state.end, target.item.id, nearestSide(target.item, target.at));
}
/**
 * A finger on a connector's end (A1): Vue Flow takes an end by the mouse only, though the gesture it starts follows touches too. The
 * touch goes on as that mouse press, before the board would pan with it.
 */
function onTouchStart(event: TouchEvent) {
  const end = (event.target as Element | null)?.closest?.(".vue-flow__edgeupdater");
  const touch = event.touches[0];
  if (!end || !touch || event.touches.length > 1 || !props.canEdit) return;
  event.preventDefault();
  event.stopPropagation();
  end.dispatchEvent(new MouseEvent("mousedown", { bubbles: true, cancelable: true, button: 0, clientX: touch.clientX, clientY: touch.clientY, view: window }));
}

/** A node's box on the board as Vue Flow measured it. */
const nodeBox = (node: GraphNode): Geometry => ({ x: node.computedPosition.x, y: node.computedPosition.y, width: node.dimensions.width, height: node.dimensions.height });
/** Where a connector attaches to a node's side: the point of that handle Vue Flow ends its edges at, the side's middle without one. */
function portOf(node: GraphNode, side: BoardSide): Port {
  const handle = node.handleBounds.source?.find(entry => entry.id === side);
  if (!handle) return { ...sideMiddle(nodeBox(node), side), side };
  const x = node.computedPosition.x + handle.x, y = node.computedPosition.y + handle.y;
  return { side, x: side === "left" ? x : side === "right" ? x + handle.width : x + handle.width / 2, y: side === "top" ? y : side === "bottom" ? y + handle.height : y + handle.height / 2 };
}
/**
 * The connector being drawn as it will be (A3, A5, A6): from its fixed end to the card it would land at, at the side it would take
 * there (ringed), else to `free`; in the order of its own ends, so the route and the arrowheads are the edge's own.
 */
function previewOf(state: Drawing, fixed: GraphNode | undefined, over: GraphNode | undefined, free: Point, side: BoardSide | undefined) {
  if (!fixed) return null;
  const [fixedSide, freeSide] = previewSides(nodeBox(fixed), state.fixedSide, free, over && { box: nodeBox(over), side });
  const freePort: Port = over ? portOf(over, freeSide) : { ...free, side: freeSide };
  const ends: [Port, Port] = [portOf(fixed, fixedSide), freePort];
  const [source, target] = state.moving === "source" ? [ends[1], ends[0]] : ends;
  return { source, target, look: state.look, port: over ? freePort : undefined, boxes: [nodeBox(fixed), ...(over ? [nodeBox(over)] : [])], ends: [fixed.id, over?.id] };
}
/** Vue Flow's connection line: snapped to a handle near the pointer (its side), else over the card under it (the nearest side). */
function connectionPreview(line: ConnectionLineProps) {
  const state = drawing.value;
  if (!state) return null;
  const free = { x: line.targetX, y: line.targetY };
  const handled = line.targetNode && line.targetNode.id !== state.fixed ? line.targetNode : undefined;
  const landed = [...landing.value][0];
  const over = handled ?? (landed ? flow.findNode(landed) : undefined);
  const side = handled ? handleSide(line.targetHandle?.id) : over && !state.auto ? nearestSide(nodeBox(over), free) : undefined;
  return previewOf(state, line.sourceNode, over, free, side);
}

// The selection's actions (context toolbar and keys).
const selectedIds = () => [...selected.value];
const toaster = useToast();
/** K2 (spec 2026-10-30 §7): a contact whose last card leaves the board (`removed`, read before the deletion is sent), used nowhere
 * else, may go to the trash (contactLinking.ts). */
function contactsLeft(removed: readonly string[]): Promise<string[]> {
  if (!props.placeKey) return Promise.resolve([]);
  const gone = new Set(removed), contact = (item: BoardViewItem) => (item.kind === "object" && item.props?.typeId === "atlas.contact" ? item.props.objectId : undefined);
  const left = new Set(items.value.flatMap(item => (gone.has(item.id) ? [] : [contact(item)])));
  const asked = [...new Set(removed.map(id => byId.value.get(id)).flatMap(item => (item && contact(item) ? [contact(item)!] : [])))].filter(id => !left.has(id))
    .map(async id => ((await contactRemoved(id, liveObjects.get(id)?.title || t.canvas.contact, { kind: "board", id: props.placeKey! }, 0)) === "trashed" ? [id] : []));
  // The ids that went to the trash: „Zpět“ on the deletion brings them back.
  return Promise.all(asked).then(ids => ids.flat());
}
function removeSelection(cut = false) {
  if (!props.canEdit || !selected.value.size) return;
  const count = selected.value.size;
  const removed = selectedIds();
  const cards = onlyCards(selectedItems.value);
  const trashed = cut !== true ? contactsLeft(removed) : Promise.resolve<string[]>([]);
  send(deleteChanges(removed));
  const deletion = gestures;
  // „Zpět“ undoes the deletion only while nothing of mine came after it (undo would take that instead).
  toaster.success(cards ? t.canvas.removed(count) : t.canvas.deleted(count),
    { undo: () => { if (gestures === deletion) { undo(); void trashed.then(ids => contactsRestored(ids)); } } });
  select([]);
  focusSurface();
}
function duplicate() {
  if (!props.canEdit || !selected.value.size) return;
  const { changes, ids } = duplicateChanges(items.value, withMembers(selectedIds(), items.value), newId);
  send(changes);
  select(ids);
}
// Groups (objects spec §5.4): Ctrl+G around the selection, Ctrl+Shift+G dissolves it, a collapsed group stands for its members.
function group() {
  const id = newId();
  const changes = props.canEdit ? groupChanges(items.value, selectedIds(), id, topZ(items.value)) : [];
  if (!changes.length) return;
  send(changes);
  select([id]);
}
function ungroup() {
  const changes = ungroupChanges(items.value, selectedIds());
  send(changes);
  if (changes.length) select(changes.flatMap(change => (change.type === "parent" ? [change.itemId] : [])));
}
function setCollapsed(collapsed: boolean) {
  const groups = selectedItems.value.filter(item => item.kind === "group").map(item => item.id);
  send(styleChanges(items.value, groups, { props: { collapsed: collapsed || null } }));
  if (collapsed) select(groups);
}
/** „Převést na stránku“: the host creates the page of the note's Markdown, then a card of it takes the note's place. */
async function convertToPage(id: string) {
  const note = byId.value.get(id);
  const page = note?.kind === "note" && props.canEdit ? await props.createPage?.(note.text ?? "") : null;
  if (!page) return;
  const changes = noteToPageChanges(items.value, id, page.id, newId);
  const card = changes.find(change => change.type === "create");
  send(changes);
  // A conversion says so with „Zpět“ (P6): the note comes back in the card's place. The page stays in its space (trashing it needs
  // the tree's versions, which the board does not hold), and the toast says so.
  const conversion = gestures;
  toaster.success(t.canvas.converted, { undo: () => { if (gestures === conversion) undo(); } });
  if (card?.type === "create") select([card.item.id]);
}
const reorder = (where: "front" | "back") => send(reorderChanges(items.value, selectedIds(), where));
const changeSettings = (settings: Partial<BoardSettings>) => send([{ type: "settings", settings }]);
/** „Přejít na…“ a date of the timeline: the view travels so that the date is in the middle. */
function goToDate(date: string) {
  const timeline = props.settings.timeline;
  if (timeline) centreOn({ x: xOfDate(timeline, date), y: centre().y });
}
function restyle(patch: Parameters<typeof styleChanges>[2], run?: string) {
  // A connector's look restyled from the toolbar is the look the next one takes.
  if (selectedItems.value.some(item => item.kind === "connector")) {
    connectorStyle = rememberConnectorStyle(connectorStyle, patch);
    saveConnectorStyle(connectorStyle);
  }
  const ids = selectedIds();
  send(styleChanges(items.value, ids, patch), [], run && `${run}:${ids.join()}`);
}
/** „Vybrat obsah“: the members of the one selected frame or group. */
function selectMembers() {
  const members = single.value ? membersOf(single.value, items.value) : [];
  if (members.length) select(members);
}
const align = (how: Alignment) => send(alignChanges(items.value, selectedIds(), how));
const distribute = (axis: "horizontal" | "vertical") => send(distributeChanges(items.value, selectedIds(), axis));
// „Uspořádat jako architekturu“ (BoardBlockHost.layout, a deployment board): offered where the selection, or with none the board, holds
// cards the host lays out; its changes go out as one batch, so one undo puts every card back.
const canArrange = computed(() => { const layout = props.blockHost?.layout; return !!layout && props.canEdit && (selectedItems.value.length ? selectedItems.value : items.value).some(layout.offers); });
async function layOut() {
  const layout = props.blockHost?.layout;
  if (!layout || !canArrange.value) return;
  // The moves are made for the board as it was read: when it changed while the pages were read (a colleague's move), once more over
  // the new one; changed again, nothing is sent and the person is told.
  for (let attempt = 0; attempt < 2; attempt++) {
    const board = items.value, changes = await layout.changes(board, selectedIds());
    if (items.value !== board) continue;
    if (changes.length) send(changes);
    else toaster.success(t.canvas.arrangeNothing);
    return;
  }
  toaster.warning(t.canvas.arrangeChanged);
}

// Navigation (spec §2): the wheel pans, Ctrl/⌘ + wheel and a pinch zoom at the cursor at once; buttons and keys travel there.
function onWheel(event: WheelEvent) {
  if ((event.target as Element).closest(".nowheel, .vue-flow__minimap")) return;
  event.preventDefault();
  const box = rect();
  void flow.setViewport(wheelViewport(viewport.value, event, { x: event.clientX - box.left, y: event.clientY - box.top }));
}
const fading = ref(false);
function travelTo(next: Viewport) {
  const el = surface.value;
  // d3 interpolates a travel over its element's own size: one without a size (not laid out yet, hidden) would get a NaN view.
  const pane = flow.d3Selection.value?.node() as HTMLElement | null | undefined;
  const sized = !!pane && pane.clientWidth > 0 && pane.clientHeight > 0;
  if (!el || !sized || prefersReducedMotion(el)) {
    // Reduced motion: the view changes at once and its layer fades in, it does not snap.
    void flow.setViewport(next);
    fading.value = false;
    requestAnimationFrame(() => { fading.value = true; });
    return;
  }
  const duration = motionMs(el, "--atlas-motion-slow");
  // By its time, not its promise: d3 never settles a travel that a wheel or another travel interrupts.
  travelling.value = true;
  clearTimeout(travelTimer);
  travelTimer = setTimeout(() => { travelling.value = false; }, duration);
  void flow.setViewport(next, { duration, ease: travel });
}
function zoomTo(zoom: number) { const box = rect(); travelTo(zoomAt(viewport.value, zoom, { x: box.width / 2, y: box.height / 2 })); }
const stepZoom = (direction: 1 | -1) => zoomTo(zoomStep(viewport.value.zoom, direction));
function fit(what: "content" | "selection", maxZoom = 2, instant = false, minZoom = props.fitMinZoom) {
  // A frame's or group's name stands above its box: fitted with it.
  const boxes = (what === "selection" ? selectedItems.value : cards.value).filter(item => item.kind !== "connector")
    .map(item => (item.kind === "frame" || item.kind === "group" ? { ...geometryOf(item), y: item.y - TITLE_ROOM, height: item.height + TITLE_ROOM } : item));
  const box = rect();
  if (!box.width || !box.height) return;
  // On a narrow board the rail is a bar along the bottom: the content is fitted into the room above it; on a wide one the margin
  // keeps the content clear of the rail at the left.
  const room = compact.value ? { width: box.width, height: Math.max(1, box.height - RAIL_BAR) } : box;
  const next = fitViewport(boxes.length ? boundsOf(boxes) : null, room, { maxZoom, minZoom, padding: compact.value ? 24 : 72 });
  if (instant) void flow.setViewport(next); else travelTo(next);
}
/**
 * A board opens fitted to its content, at most at 100 % (spec §2), and is fitted again, at once, when its pane gets its real size
 * or another one before the person first touches the board (a pointer, a key, the wheel); never after.
 */
const OPEN_MIN_ZOOM_COMPACT = 0.4;
let touched = false, fittedSize = "", placeTaken = false;
function fitOpened(instant: boolean) {
  const box = rect(), measured = `${box.width}x${box.height}`;
  // Whatever happens below, the view is the opening one once this returns.
  queueMicrotask(() => { opened.value = true; });
  if (touched || !box.width || !box.height || measured === fittedSize) return;
  // Back on a board the person left (BD2-3): its view and selection, the key on the first selected item; no fitting after that.
  const place = !placeTaken && props.placeKey ? readBoardPlace(props.placeKey) : undefined;
  placeTaken = true;
  if (place) {
    touched = true;
    void flow.setViewport(place.viewport);
    const alive = place.selected.filter(id => items.value.some(item => item.id === id));
    if (alive.length) { select(alive); focusItem(alive[0]!); }
    return;
  }
  fittedSize = measured;
  // On a phone a whole board fitted into the width is unreadable (6 %): it opens at a readable zoom, the rest is panned to (3.6 review F30).
  fit("content", 1, instant, compact.value ? Math.max(props.fitMinZoom ?? 0, OPEN_MIN_ZOOM_COMPACT) : props.fitMinZoom);
}
const onPaneReady = () => fitOpened(false);

// Keyboard (spec §3.1–3.2): only while the focus is not in a text field.
// A slider (an overview's level) keeps its arrows and letters.
const typing = (target: EventTarget | null) => !!(target as Element | null)?.closest?.("input, textarea, select, [role='slider'], [contenteditable]:not([contenteditable='false'])");
const arrows: Record<string, [number, number]> = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };
/** The controls of a component at rest (a link, a disclosure, a reveal, a download): Tab reaches them from their item. */
// Not a live requirement: its check, chip and pencil keep their own way through Tab (BoardBlock `live`).
const innerControls = (node: Element) => [...node.querySelectorAll<HTMLElement>(":is([data-board-block]:not(.is-editing, [data-block-type=requirement]), [data-deployment-card]) :is(a[href], button, summary, [tabindex='0'])")]
  .filter(control => !control.matches(":disabled") && control.getClientRects().length > 0);
/** Ctrl+Z, Ctrl+Shift+Z and Ctrl+Y: on the surface and in the selection's context toolbar alike (P6). */
function undoKeys(event: KeyboardEvent) {
  const key = event.key.toLowerCase();
  if (!(event.ctrlKey || event.metaKey) || (key !== "z" && key !== "y")) return false;
  event.preventDefault();
  if (props.canEdit) { if (key === "y" || event.shiftKey) redo(); else undo(); }
  return true;
}
function onToolbarKeydown(event: KeyboardEvent) { if (!typing(event.target)) undoKeys(event); }
/**
 * What is typed from the start of a text's editing until its editor has the keys (a new note, a frame title: the editor mounts and takes
 * the focus a few frames later): every key waits here in order and goes into that editor once it holds the focus and its caret, instead of
 * choosing tools, locking the board or being lost while the editor is not yet listening (D12 „psaní hned“).
 */
let typedAhead = "";
let awaitingEditor = false, awaitingSince = 0;
/** Esc came while keys still waited: the editing ends once they are in (nothing typed is lost to an early Esc). */
let endAfterKeys = false;
const typedKey = (event: KeyboardEvent) => event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey && !event.isComposing;
/**
 * The item being edited is a text (or is new: not among the items until its creation came back, a render later), or a page's preview
 * entered in place, whose live editor loads first (3.6): the letters typed meanwhile wait for it the same way.
 */
function editsText() {
  const edited = editing.value ? byId.value.get(editing.value) : undefined;
  return !!editing.value && (!edited || textKinds.has(edited.kind) || edited.kind === "connector" || edited.kind === "page");
}
function holdKeys(event: KeyboardEvent) {
  // Typing that went on after the item it was meant for was removed: every key of it is swallowed until the typing pauses.
  if (lostTyping > performance.now() && typedKey(event) && !typing(event.target)) {
    event.preventDefault();
    event.stopImmediatePropagation();
    lostTyping = performance.now() + 800;
    return;
  }
  // Letters typed after `/` while the command panel's chunk loads are its search's (BD2-9), not tool keys.
  if (panel.value && typedKey(event) && !typing(event.target) && !(event.target as Element | null)?.closest?.("[data-board-command-panel]")) {
    event.preventDefault();
    event.stopImmediatePropagation();
    panelTyped.value += event.key;
    return;
  }
  if (awaitingEditor && event.key === "Escape" && editsText() && (typedAhead || !typing(event.target))) {
    event.preventDefault();
    event.stopImmediatePropagation();
    // Keys waiting for an editor that has the focus: the editing ends after they are in. Otherwise it ends now (endEdit keeps them).
    const active = document.activeElement;
    if (typedAhead && active?.getAttribute("contenteditable") === "true" && itemElement(editing.value!)?.contains(active)) endAfterKeys = true;
    else endEdit(editing.value!);
    return;
  }
  if (!awaitingEditor || !typedKey(event) || !editsText()) return;
  // Another field (a dialog, the title) keeps its own keys. The edited text's own editor gets them once it took the waiting ones, or,
  // should its focus never have been seen arriving, a while after the editing began.
  if (typing(event.target) && (!itemElement(editing.value!)?.contains(event.target as Node) || performance.now() - awaitingSince > 1500)) return;
  event.preventDefault();
  event.stopImmediatePropagation();
  typedAhead += event.key;
}
function takeTypedAhead(event: FocusEvent) {
  const target = event.target as HTMLElement | null;
  if (!awaitingEditor || !editing.value || target?.getAttribute("contenteditable") !== "true" || !itemElement(editing.value)?.contains(target)) return;
  // Once the editor has its caret (a task later), as typed input: through its text input handlers, so its rules apply as to typing.
  const view = (target as HTMLElement & { editor?: { view: EditorView } }).editor?.view;
  setTimeout(() => {
    const text = typedAhead;
    typedAhead = "";
    awaitingEditor = false;
    const end = endAfterKeys;
    endAfterKeys = false;
    if (!text || !view || view.isDestroyed) return;
    const { from, to } = view.state.selection, insert = () => view.state.tr.insertText(text, from, to);
    if (!view.someProp("handleTextInput", handle => handle(view, from, to, text, insert))) view.dispatch(insert());
    if (end) target!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", keyCode: 27, bubbles: true, cancelable: true }));
  });
}
onMounted(() => window.addEventListener("keydown", holdKeys, true));
onBeforeUnmount(() => window.removeEventListener("keydown", holdKeys, true));
function onKeydown(event: KeyboardEvent) {
  if (typing(event.target)) return;
  // A page's preview being entered (its editor loads): the keys until the editor has them are not the board's (the letters wait for it in
  // holdKeys); Esc gives it up.
  if (editing.value && byId.value.get(editing.value)?.kind === "page") { event.preventDefault(); if (event.key === "Escape") endEdit(editing.value); return; }
  const target = event.target as HTMLElement | null;
  // Linking (L) owns the keys until it ends: no control inside a component takes them meanwhile.
  const nodeElement = linking.value ? null : target?.closest?.<HTMLElement>(".vue-flow__node") ?? null;
  // A control inside a component keeps its own Enter and Space (open the link, open the disclosure, reveal).
  // Ctrl+Enter is the item's „Otevřít“ wherever its focus is (a control inside it would take it as its own Enter).
  if (nodeElement && target !== nodeElement && (event.key === "Enter" || event.key === " ") && !(event.key === "Enter" && (event.ctrlKey || event.metaKey)) && innerControls(nodeElement).includes(target!)) return;
  // Tab goes through a component's own controls before the next item; Shift+Tab back to the item itself.
  if (nodeElement && event.key === "Tab" && !event.ctrlKey && !event.metaKey && !event.altKey) {
    const controls = innerControls(nodeElement), at = controls.indexOf(target!);
    const step = event.shiftKey ? (at > 0 ? controls[at - 1] : at === 0 ? nodeElement : undefined) : controls[at + 1];
    if (controls.length && step) { event.preventDefault(); step.focus({ preventScroll: true }); return; }
  }
  const mod = event.ctrlKey || event.metaKey, key = event.key.toLowerCase();
  const handled = () => event.preventDefault();
  // A key on an item reached by Tab acts on that item.
  const focused = (event.target as Element | null)?.closest?.(".vue-flow__node, .vue-flow__edge")?.getAttribute("data-id");
  if (linking.value && linkKey(event)) { handled(); return; }
  if (focused && !selected.value.has(focused) && !["Shift", "Control", "Alt", "Meta", "Tab"].includes(event.key)) select([focused]);
  // Tab and Shift+Tab walk the items in reading order; past the first or the last the focus leaves the board.
  if (event.key === "Tab" && !mod && !event.altKey) {
    const order = readingOrder(nodes.value.filter(node => node.focusable).map(node => ({ id: node.id, x: node.position.x, y: node.position.y, width: node.width as number, height: (node.height as number | undefined) ?? byId.value.get(node.id)?.height ?? 0 })));
    const at = order.indexOf(focused ?? single.value ?? "");
    const next = event.shiftKey ? (at < 0 ? -1 : at - 1) : at + 1;
    if (next >= order.length) { handled(); leaveBoard(); return; }
    if (next < 0) return;
    handled();
    select([order[next]!]);
    reveal(order[next]!);
    return;
  }
  // The actions menu (P2), as in the page editor: Ctrl+Space, Alt+Enter (AI rows first), Ctrl+., Shift+F10 and the menu key, on the selection.
  const altEnter = event.altKey && !mod && !event.shiftKey && event.key === "Enter";
  const menuKey = opensActions(event);
  // With nothing selected the menu is over the board itself (P11: its AI rows), where a module offers any.
  if (menuKey && (selected.value.size || hasBoardIntentions(intentionContext([], new AbortController().signal)))) { handled(); openActions(undefined, altEnter); return; }
  // „Otevřít“ of a card (P1: never Enter): its page, its board, a requirement's page.
  if (mod && !event.altKey && !event.shiftKey && event.key === "Enter" && single.value) {
    handled();
    const item = byId.value.get(single.value);
    if (item && opensAs(item)) open(item);
    return;
  }
  if (undoKeys(event)) return;
  if (mod && key === "a") {
    handled();
    // On one selected frame or group: its contents first; once more, everything.
    const members = single.value ? membersOf(single.value, items.value) : [];
    select(members.length ? members : items.value.map(item => item.id));
    return;
  }
  if (mod && key === "d") { handled(); duplicate(); return; }
  if (mod && key === "g") { handled(); if (event.shiftKey) ungroup(); else group(); return; }
  // Ctrl/⌘+K is the application's search on a board too (not handled here); the board's commands are on / and in the actions menu (Ctrl+.).
  if (mod && (event.key === "'" || event.code === "Quote")) { handled(); changeSettings({ snapToGrid: !props.settings.snapToGrid }); return; }
  if (event.shiftKey && !mod && (event.code === "Digit1" || event.code === "Digit2" || event.code === "Digit0")) {
    handled();
    if (event.code === "Digit0") zoomTo(1); else fit(event.code === "Digit1" ? "content" : "selection", 4);
    return;
  }
  // Alt+F10, as the page editor's block controls: into the selection's context toolbar (BD-3).
  if (event.altKey && !mod && event.key === "F10") {
    handled();
    surface.value?.closest("[data-board-canvas]")?.querySelector<HTMLElement>("[data-board-context-toolbar] button:not(:disabled)")?.focus({ preventScroll: true });
    return;
  }
  if (event.altKey && !mod && arrows[event.key] && selected.value.size && props.canEdit) {
    // Prevented before anything else: Alt with the left arrow is the browser's Back.
    handled();
    const [dx, dy] = arrows[event.key]!, step = event.shiftKey ? 10 : 1;
    resizeBy(dx * step, dy * step);
    return;
  }
  if (mod || event.altKey) return;
  if (event.key === "/") { handled(); openPanel(false); return; }
  const arrow = arrows[event.key];
  if (arrow) {
    handled();
    // Read-only, arrows have nothing to move: they walk to the nearest card in their direction (derived boards, the relations graph).
    if (!props.canEdit && single.value) { walkTo(directions[event.key]!); return; }
    const step = event.shiftKey ? 10 : 1;
    if (selected.value.size) nudgeBy(arrow[0] * step, arrow[1] * step);
    else void flow.setViewport({ ...viewport.value, x: viewport.value.x - arrow[0] * 60, y: viewport.value.y - arrow[1] * 60 });
    return;
  }
  if (event.key === "Delete" || event.key === "Backspace") { handled(); removeSelection(); return; }
  if (event.key === "Escape") {
    handled();
    tool.value = "select";
    // From what is selected to the frame or group holding it; from the top level to nothing.
    const up = escapeSelection(selectedIds(), items.value);
    select(up);
    if (up.length) focusItem(up[0]!); else focusSurface();
    return;
  }
  // A creation tool chosen by its key places its item with Enter, at the pointer or in the middle of the view (BD-12).
  const placing = activeTool.value;
  if (event.key === "Enter" && (placing === "note" || placing === "text" || placing === "shape" || placing === "frame")) {
    handled();
    create(placing, pointer ? toBoard(pointer.x, pointer.y) : centre());
    return;
  }
  // D1: Enter and F2 start the editing of the selected item, as a double-click does; a card with nothing to edit keeps them quiet.
  // A reader's Enter is the card's reading action (J4.2): it opens what the card opens.
  if ((event.key === "Enter" || event.key === "F2") && single.value) {
    handled();
    if (props.canEdit || event.key === "Enter") startEdit(single.value);
    return;
  }
  // Space held pans; a short tap of it over empty board opens the command panel (spec §5.2).
  if (event.key === " ") { handled(); if (!event.repeat) spaceTap = performance.now(); keys.value.space = true; return; }
  if (event.key === "+" || event.key === "=") { handled(); stepZoom(1); return; }
  if (event.key === "-") { handled(); stepZoom(-1); return; }
  // L on one selected item connects it from the keyboard, on a selected connector moves its end (Shift: its start); without one it is the connector tool.
  if (key === "l" && props.canEdit && single.value) { handled(); startLinking(single.value, event.shiftKey ? "source" : "target"); return; }
  if (boardToolKeys[key]) { handled(); tool.value = boardToolKeys[key]!; return; }
  if (!props.canEdit) return;
  if (key === "p") { handled(); void pickPages(); }
  else if (key === "i") { handled(); void pickIssues(); }
  else if (key === "c") { handled(); void pickContacts(); }
  else if (key === "k") { handled(); openComponents(); }
}
/** Shows the item (the view travels to it when it is out of view) and gives it the focus. */
function reveal(id: string) {
  const item = byId.value.get(id), view = visibleArea();
  if (item && (item.x < view.x || item.y < view.y || item.x + item.width > view.x + view.width || item.y + item.height > view.y + view.height)) centreOn({ x: item.x + item.width / 2, y: item.y + item.height / 2 });
  focusItem(id);
}
const directions: Record<string, Direction> = { ArrowLeft: "left", ArrowRight: "right", ArrowUp: "up", ArrowDown: "down" };
function walkTo(direction: Direction) {
  const from = single.value && byId.value.get(single.value);
  if (!from || from.kind === "connector") return;
  const hidden = collapsedOwners(items.value);
  const to = nearestInDirection(geometryWithId(from), cards.value.filter(item => item.id !== from.id && !hidden.has(item.id)).map(geometryWithId), direction);
  if (to) { select([to]); reveal(to); }
}
function startLinking(id: string, end: "source" | "target") {
  const item = byId.value.get(id);
  if (item?.kind !== "connector") linking.value = { from: id };
  // The arrows go on from the end that moves.
  else if (item.from && item.to) linking.value = { from: end === "target" ? item.from : item.to, to: end === "target" ? item.to : item.from, edge: id, end };
  showDropTarget(new Set(linking.value?.edge ? [linking.value.to!] : []));
}
function stopLinking() { linking.value = null; showDropTarget(new Set()); presence(); }
const geometryWithId = (item: BoardItem) => ({ id: item.id, ...geometryOf(item) });
/** A key while connecting from the keyboard: arrows choose the item, Enter connects, Esc gives up; false lets any other key end it. */
function linkKey(event: KeyboardEvent): boolean {
  const state = linking.value!;
  const direction = directions[event.key];
  if (direction) {
    const start = byId.value.get(state.to ?? state.from);
    // Not the members hidden in a collapsed group, as the arrows walking the board skip them (walkTo).
    const hidden = collapsedOwners(items.value);
    const candidates = cards.value.filter(item => item.id !== state.from && !hidden.has(item.id)).map(geometryWithId);
    const to = start && nearestInDirection(geometryWithId(start), candidates, direction);
    if (to) { linking.value = { ...state, to }; showDropTarget(new Set([to])); reveal(to); presence(); }
    return true;
  }
  if (event.key === "Enter") {
    const connector = state.edge ? byId.value.get(state.edge) : undefined;
    stopLinking();
    if (connector) {
      // Back where it was: nothing, its side stays.
      if (state.to && state.to !== (state.end === "target" ? connector.to : connector.from)) reconnect(connector, state.end!, state.to, undefined);
      focusItem(connector.id);
      return true;
    }
    const id = newId();
    // The keys go on with the new connector (Enter names it), not with the item the arrows ended on.
    if (state.to) { send(connectChanges(items.value, { id, from: state.from, to: state.to, style: connectorStyle })); select([id]); focusItem(id); }
    return true;
  }
  if (event.key === "Escape") { stopLinking(); focusItem(state.edge ?? state.from); return true; }
  if (["Shift", "Control", "Alt", "Meta"].includes(event.key)) return true;
  stopLinking();
  return false;
}
/** A board item's box for the others' connectors in progress (BoardPresence). */
const presenceBox = (id: string): Geometry | undefined => byId.value.get(id);
/** The connector L would make or move, drawn while the arrows choose (A9): with facing sides, as the one it sends (the moved end's side unset). */
const linkPreview = computed(() => {
  const state = linking.value;
  if (!state?.to) return null;
  const connector = state.edge ? byId.value.get(state.edge) : undefined;
  const look: Drawing = connector ? drawingOf(connector, state.end!) : { look: { ...defaultProps("connector"), ...connectorStyle }, fixed: state.from, moving: "target", auto: true };
  const over = flow.findNode(state.to);
  return over ? previewOf(look, flow.findNode(state.from), over, sideMiddle(nodeBox(over), "top"), undefined) : null;
});
/** What the live region says: connecting from the keyboard, else the selection. */
const sizeNote = ref("");
let sizeNoteTimer: ReturnType<typeof setTimeout> | undefined;
function say(note: string) {
  // The same words again (a limit held) differ by an invisible character, so the live region speaks them again.
  sizeNote.value = sizeNote.value === note ? note + String.fromCharCode(0x200b) : note;
  clearTimeout(sizeNoteTimer);
  sizeNoteTimer = setTimeout(() => { sizeNote.value = ""; }, 1500);
}
onBeforeUnmount(() => clearTimeout(sizeNoteTimer));
const announcement = computed(() => {
  if (sizeNote.value) return sizeNote.value;
  const state = linking.value;
  if (state) {
    const from = byId.value.get(state.from), to = state.to ? byId.value.get(state.to) : undefined;
    if (state.edge) return t.canvas.reconnecting(to ? itemLabel(to) : undefined);
    return t.canvas.connecting(from ? itemLabel(from) : undefined, to ? itemLabel(to) : undefined);
  }
  return selectedItems.value.length === 1 ? itemLabel(selectedItems.value[0]!) : selectedItems.value.length ? t.canvas.selected(selectedItems.value.length) : "";
});
function onKeyup(event: KeyboardEvent) {
  if (event.key === " " && !typing(event.target)) {
    keys.value.space = false;
    if (spaceTap && performance.now() - spaceTap < 300 && !selected.value.size) openPanel(false);
    spaceTap = 0;
  }
  keys.value.shift = event.shiftKey;
  keys.value.alt = event.altKey;
}
function onWindowKey(event: KeyboardEvent) { keys.value.shift = event.shiftKey; keys.value.alt = event.altKey; if (event.type === "keyup" && event.key === " ") keys.value.space = false; }
function onWindowBlur() { keys.value = { shift: false, alt: false, space: false }; spaceTap = 0; }

// The command panel (spec §5.2): at the pointer, or at the selection; its entries come from boardCommands.ts.
// `components`: only the components (the rail's Komponenta, K), beside the rail, inserted where they hide nothing.
const panel = shallowRef<{ at: Point; board: Point; components?: boolean } | null>(null);
/** What was typed between `/` and the panel's search taking the keys (holdKeys): the search starts with it. */
const panelTyped = ref("");
const commands = computed(() => boardCommands({
  canEdit: props.canEdit, selection: selectedItems.value, modules: blockModules.value,
  can: { pickPage: !!props.pickPage, pickIssues: !!props.pickIssues, pickContacts: !!props.pickContacts, createPage: !!props.createPage,
    links: !!props.blockHost?.links, waiting: !!props.blockHost?.links && !!props.blockHost.projector, arrange: canArrange.value },
}).filter(entry => !panel.value?.components || entry.section === "component"));
// Every item by name, for finding it from the panel; read only while the panel is open (an object card is named by its object).
const finds = computed(() => {
  if (!panel.value || panel.value.components) return [];
  // A connector by the name its edge is drawn with; one merged into a collapsed group is not drawn on its own and is left out.
  const drawn = new Map(restingEdges.value.map(edge => [edge.id, edge.ariaLabel]));
  return itemCommands(items.value, item => (item.kind === "object" ? objectLabel(item) : item.kind === "connector" ? drawn.get(item.id) ?? undefined : itemLabel(item)));
});
function openPanel(atSelection: boolean) {
  loadBlockModules();
  const box = rect(), v = viewport.value;
  const chosen = selectedItems.value.filter(item => item.kind !== "connector");
  const from = atSelection && chosen.length ? boundsOf(chosen) : undefined;
  const client = from ? { x: box.left + (from.x + from.width / 2) * v.zoom + v.x, y: box.top + (from.y + from.height) * v.zoom + v.y }
    : pointer ?? { x: box.left + box.width / 2, y: box.top + box.height / 2 };
  panelTyped.value = "";
  panel.value = { at: { x: client.x - box.left, y: client.y - box.top }, board: toBoard(client.x, client.y) };
}
function openComponents() {
  if (!props.canEdit) return;
  loadBlockModules();
  const box = rect(), button = surface.value?.closest("[data-board-canvas]")?.querySelector("[data-board-tool=component]")?.getBoundingClientRect();
  panelTyped.value = "";
  panel.value = { at: button ? { x: button.right - box.left + 8, y: button.top - box.top } : { x: 56, y: 8 }, board: centre(), components: true };
}
function closePanel() { panel.value = null; focusSurface(); }

// „Co s tím jde dělat“ (P2): the page editor's own menu (openMarkdownIntentionsMenu) on the selection, from the right button (Shift
// keeps the browser's), Ctrl+Space, Alt+Enter, Ctrl+., Shift+F10 or the menu key. The focus stays on the item; while the menu is open
// it takes the keys before anything else on the page (`menuKeys`), so a host's own Esc or Enter (the relations graph's) waits for it.
// Its rows: the selection's actions (boardCommands.actionCommands, the panel's ids), then what modules add (boardIntentions.ts, where
// AI puts its rows only while it can answer).
let actionsMenu: MarkdownIntentionsMenu | undefined;
/** Counts the menus asked for: a load that a later gesture or a close overtook opens nothing. */
let menuRun = 0;
let menuLoading = false;
// The editor's tokens live on `.md-editor` with its theme; the menu in the body copies them from this element (as ActionMenu does).
const editorTheme = useEditorTheme();
const menuTheme = ref<HTMLElement>();
function offerOf(item: BoardViewItem | undefined): ItemOffer {
  return item ? { edit: !!pageToEdit(item) || editsInPlace(item), open: opensAs(item), link: !!addressOf(item) } : {};
}
/** The board's own rows for these items. */
function ownActions(chosen: readonly BoardViewItem[]) {
  const can = { pickPage: !!props.pickPage, pickIssues: !!props.pickIssues, pickContacts: !!props.pickContacts, createPage: !!props.createPage, arrange: canArrange.value };
  return actionCommands({ canEdit: props.canEdit, selection: chosen, modules: [], can }, offerOf(chosen.length === 1 ? chosen[0] : undefined));
}
const intentionContext = (chosen: readonly BoardViewItem[], signal: AbortSignal) => ({ items: chosen, board: items.value, canEdit: props.canEdit, signal });
function actionRows(signal: AbortSignal): (readonly MarkdownIntention[] | Promise<readonly MarkdownIntention[]>)[] {
  const chosen = selectedItems.value;
  const own = (chosen.length ? ownActions(chosen) : []).map((entry): MarkdownIntention => ({
    id: entry.id, label: entry.title, icon: `svg:${entry.icon}`, keywords: entry.keywords, ...(entry.description && { description: entry.description }), run: () => actions[entry.id]?.(),
  }));
  return [own, ...boardIntentionRows(intentionContext(chosen, signal))];
}
/** The open menu's keys (arrows, Enter, Esc, typing filters), in the capture phase of the document. */
function menuKeys(event: KeyboardEvent) {
  if (actionsMenu?.key(event)) { event.preventDefault(); event.stopPropagation(); }
}
/**
 * Opens the menu at `at` (a pointer) or at the first selected item (with none, over the whole board at its corner), `aiFirst` on
 * Alt+Enter as in the page editor; at once when its chunk is in (every board preheats it), else once it is, unless another gesture or
 * a close came meanwhile.
 */
function openActions(at?: Point, aiFirst = false) {
  closeActions();
  const first = selectedIds()[0];
  const run = menuRun;
  const corner = () => { const box = surface.value?.getBoundingClientRect() ?? new DOMRect(); return new DOMRect(box.left + 16, box.top + 16, 0, 0); };
  const anchor = () => (at ? new DOMRect(at.x, at.y, 0, 0) : first ? (itemElement(first) ?? surface.value)?.getBoundingClientRect() ?? new DOMRect() : corner());
  const show = (chunk: NonNullable<ReturnType<typeof actionsMenuReady>>) => {
    if (run !== menuRun) return;
    menuLoading = false;
    if (!surface.value || (first ? !selected.value.has(first) : selected.value.size)) return;
    const abort = new AbortController();
    const menu: MarkdownIntentionsMenu = chunk.openActionsMenu({ document, anchor, contextElement: menuTheme.value ?? surface.value, rows: actionRows(abort.signal), aiFirst,
      onClose: () => {
        abort.abort();
        document.removeEventListener("keydown", menuKeys, true);
        if (actionsMenu === menu) actionsMenu = undefined;
      } });
    actionsMenu = menu;
    document.addEventListener("keydown", menuKeys, true);
  };
  const chunk = actionsMenuReady();
  if (chunk) show(chunk);
  else { menuLoading = true; void loadActionsMenu().then(show, () => { if (run === menuRun) menuLoading = false; }); }
}
function closeActions() { menuRun++; menuLoading = false; actionsMenu?.close(); actionsMenu = undefined; }
onBeforeUnmount(closeActions);
/**
 * The right button on an item: its menu, the item selected first. The browser's menu stays on the empty board, in a text being
 * edited, on a link or an image inside an item (P1: a link opens as a link) and where the board offers nothing (a reader's note, P10).
 */
function onContextMenu(event: MouseEvent) {
  // A menu still open (or on its way) got no pointer down since: this is the menu key's own event (Windows sends it on the key's
  // release, after the keydown opened the menu), which neither opens a second menu nor the browser's.
  if (actionsMenu || menuLoading) { event.preventDefault(); return; }
  const target = event.target as Element | null;
  if (event.shiftKey || typing(target) || target?.closest?.("a[href], img")) return;
  const hit = target?.closest?.<HTMLElement>("[data-board-edge-label], .vue-flow__node, .vue-flow__edge");
  const id = hit?.dataset.boardEdgeLabel ?? hit?.dataset.id;
  if (!id || !byId.value.has(id)) return;
  const chosen = selected.value.has(id) ? selectedItems.value : [byId.value.get(id)!];
  if (!ownActions(chosen).length && !hasBoardIntentions(intentionContext(chosen, new AbortController().signal))) return;
  event.preventDefault();
  if (!selected.value.has(id)) select([id]);
  if (!itemElement(id)?.contains(document.activeElement)) focusItem(id);
  openActions({ x: event.clientX, y: event.clientY });
}
const alignCommands = ["left", "center", "right", "top", "middle", "bottom"] as const;
const actions: Record<string, () => void> = {
  "selection:edit": () => { if (single.value) startEdit(single.value); },
  "selection:open": () => { const item = single.value && byId.value.get(single.value); if (item) open(item); },
  "selection:copy-link": () => { const item = single.value && byId.value.get(single.value); if (item) void copyLink(item); },
  "place:page": () => void pickPages(),
  "board:settings": () => { settingsOpen.value = true; },
  "board:arrange": () => { void layOut(); },
  "board:detail": () => emit("openDetail", null),
  "selection:group": group, "selection:ungroup": ungroup, "selection:collapse": () => setCollapsed(true), "selection:expand": () => setCollapsed(false),
  "selection:duplicate": duplicate, "selection:delete": removeSelection,
  "selection:detail": () => { if (single.value) emit("openDetail", single.value); },
  "selection:to-page": () => { if (single.value) void convertToPage(single.value); },
  ...Object.fromEntries(alignCommands.map(how => [`selection:align-${how}`, () => align(how)])),
};
/** Brings an item found by name to the middle of the view, at a zoom it can be read at, selects it and gives it the key. */
function showFound(id: string) {
  const found = byId.value.get(id);
  if (!found) return;
  // A connector has no box of its own: the view goes to the middle between its ends.
  const ends = found.kind === "connector" ? [byId.value.get(found.from ?? ""), byId.value.get(found.to ?? "")].filter(end => !!end) : [];
  const item = ends.length ? boundsOf(ends) : found;
  touched = true;
  const box = rect(), zoom = Math.min(1, Math.max(viewport.value.zoom, 0.7));
  travelTo({ x: box.width / 2 - (item.x + item.width / 2) * zoom, y: box.height / 2 - (item.y + item.height / 2) * zoom, zoom });
  select([id]);
  focusItem(id);
}
function runCommand(id: string) {
  const at = panel.value?.board ?? centre();
  closePanel();
  if (id.startsWith("create:")) create(id.slice(7) as Parameters<typeof create>[0], at);
  else if (id.startsWith("component:")) onComponent(id.slice(10));
  else if (id.startsWith("pick:") || id.startsWith("links:")) onComponent(id);
  else if (id.startsWith("find:")) showFound(id.slice(5));
  else actions[id]?.();
}

// Neighbour highlighting (spec §5.3): the hovered node's relations up and down (Shift: the whole chain), or the path between two
// selected nodes; the rest dims. Nothing while a gesture runs, and nothing for a node without relations.
const hovered = ref<string>();
// Only the card the pointer rests on: the highlighting dims the whole board (about 20 ms of style and paint per card at 1x CPU), which a
// pointer passing over a row of cards did for each of them. Leaving a card clears it at once.
const HOVER_REST_MS = 120;
let hoverTimer: ReturnType<typeof setTimeout> | undefined;
function hoverNode(id?: string) {
  clearTimeout(hoverTimer);
  if (id) hoverTimer = setTimeout(() => { hovered.value = id; }, HOVER_REST_MS);
  else hovered.value = undefined;
}
const links = computed(() => restingEdges.value.map(edge => (edge.data?.merged
  ? { id: edge.id, from: edge.source, to: edge.target }
  : linkOf({ id: edge.id, from: edge.source, to: edge.target, props: edge.data?.item.props }))));
const highlight = steady<Highlight | null>(() => {
  if (dragging.value || dragActive.value || connecting.value) return null;
  if (hovered.value) {
    const around = neighbourhood(links.value, hovered.value, keys.value.shift);
    return around.links.size ? around : null;
  }
  const chosen = selectedItems.value.filter(item => item.kind !== "connector");
  if (chosen.length === 2) return pathBetween(links.value, chosen[0]!.id, chosen[1]!.id);
  return null;
});
onMounted(() => {
  // An opened board takes the keys at once, unless the focus already is somewhere (BD-1; the shell's landing comes too early
  // for a board that is still loading).
  landFocus(surface.value?.closest<HTMLElement>("main"));
  window.addEventListener("keydown", onWindowKey, true);
  window.addEventListener("keyup", onWindowKey, true);
  window.addEventListener("blur", onWindowBlur);
});
onBeforeUnmount(() => {
  if (props.placeKey) saveBoardPlace(props.placeKey, touched ? { viewport: viewport.value, selected: [...selected.value] } : null);
  window.removeEventListener("keydown", onWindowKey, true);
  window.removeEventListener("keyup", onWindowKey, true);
  window.removeEventListener("blur", onWindowBlur);
  clearTimeout(nudge?.timer);
  clearTimeout(motionTimer);
  clearTimeout(leaveTimer);
  clearTimeout(hoverTimer);
});
// Vue Flow keeps a selection rectangle over a marquee's result, which would swallow clicks on the items in it.
watch(() => flow.nodesSelectionActive.value, active => { if (active) flow.nodesSelectionActive.value = false; });

// Clipboard (spec §3.2): the board's own JSON plus plain text; pasted text and links go through boardPaste.ts.
function onCopy(event: ClipboardEvent, cut = false) {
  if (typing(event.target) || !event.clipboardData) return;
  const payload = copyPayload(items.value, withMembers(selectedIds(), items.value));
  if (!payload) return;
  event.preventDefault();
  event.clipboardData.setData("text/plain", payload.text);
  event.clipboardData.setData(boardClipboardType, payload.json);
  if (cut) removeSelection(true);
}
function onPaste(event: ClipboardEvent) {
  if (typing(event.target) || !event.clipboardData || !props.canEdit) return;
  event.preventDefault();
  const at = cursor ?? centre();
  const copied = readPayload(event.clipboardData.getData(boardClipboardType));
  if (copied?.length) {
    const { changes, ids } = pasteChanges(copied, at, newId, topZ(items.value));
    send(changes);
    select(ids);
    return;
  }
  void insertPasted(parsePaste(event.clipboardData.getData("text/plain"), props.pasteContext ?? appPasteContext()), at);
}
// Dropping a page from the tree, a link or text (boardDrop.ts).
function onDragOver(event: DragEvent) {
  if (!props.canEdit || !event.dataTransfer || !dropAccepts(event.dataTransfer.types)) return;
  event.preventDefault();
  event.dataTransfer.dropEffect = "copy";
}
function onDrop(event: DragEvent) {
  if (!props.canEdit || !event.dataTransfer || !dropAccepts(event.dataTransfer.types)) return;
  event.preventDefault();
  void insertPasted(readDrop(event.dataTransfer, props.pasteContext ?? appPasteContext()), toBoard(event.clientX, event.clientY));
}

// Where the context toolbar stands: above the selection's bounds, below when there is no room, inside the canvas.
const toolbarAt = computed(() => {
  if (!selectedItems.value.length || dragging.value || editing.value) return null;
  // A reader's toolbar holds only reading actions (open, detail, select a frame's content): none for anything else (contract §7).
  const only = selectedItems.value.length === 1 ? selectedItems.value[0]! : undefined;
  if (!props.canEdit && !(only && (only.kind === "page" || only.kind === "frame" || only.kind === "group" || isBoardCard(only)))) return null;
  const linked = selectedItems.value.flatMap(item => (item.kind === "connector" ? [byId.value.get(item.from ?? ""), byId.value.get(item.to ?? "")] : [item])).filter((item): item is BoardViewItem => !!item);
  if (!linked.length) return null;
  // The observed size, not rect(): read while the nodes patch, rect() forced a layout of the whole canvas mid-update (UX SB4-3).
  const b = boundsOf(linked), v = viewport.value, box = size.value;
  const left = (b.x + b.width / 2) * v.zoom + v.x, top = b.y * v.zoom + v.y, bottom = (b.y + b.height) * v.zoom + v.y;
  const above = top > 64, half = Math.min(toolbarWidth.value, box.width - 16) / 2;
  return { x: Math.min(Math.max(left, half + 8), Math.max(half + 8, box.width - half - 8)), y: above ? top - 12 : Math.min(bottom + 12, box.height - 48), above };
});
/** The toolbar's own width, so that it stays inside the canvas. */
const toolbarWidth = ref(320);
let toolbarObserver: ResizeObserver | undefined;
function measureToolbar(el: unknown) {
  toolbarObserver?.disconnect();
  if (!(el instanceof HTMLElement) || typeof ResizeObserver === "undefined") return;
  toolbarObserver = new ResizeObserver(() => { toolbarWidth.value = el.offsetWidth || toolbarWidth.value; });
  toolbarObserver.observe(el);
}
onBeforeUnmount(() => toolbarObserver?.disconnect());

/** The board-wide state the nodes and edges read for themselves (perItem): a hover, a selection or an edit wakes only those it concerns. */
function perItemState(): BoardCanvasContext["per"] {
  const isSingle = perItem(() => only(single.value)), isEditing = perItem(() => only(editing.value)), isHovered = perItem(() => only(hoveredEdge.value));
  const mark = perItem(() => highlight.value?.nodes), linkMark = perItem(() => highlight.value?.links);
  const target = perItem(() => new Map([...landing.value].map(id => [id, true])));
  return { single: id => !!isSingle(id), editing: id => !!isEditing(id), mark, linkMark, dropTarget: id => !!target(id), hoveredEdge: id => !!isHovered(id) };
}
/** The radius of a connector end's grab circle: 44 px for a finger (A7). */
const endRadius = computed(() => (touching.value ? 22 : 12));
provide(boardCanvasKey, {
  flowId, canEdit: computed(() => props.canEdit), tool: activeTool, editing, editPoint, entering, single, overview, storedHeight: id => storedHeights.value.get(id), shift: computed(() => keys.value.shift), highlight, hoveredEdge, dropTarget: landing,
  per: perItemState(),
  startEdit, saveText, commitText, endEdit, unsaved, resizing, resized, open, openTarget, blockHost: props.blockHost, saveBlock, commitBlock, remove: removeItem, editedObject,
  restyle: (id: string, patch: { props: BoardPropsPatch }) => send(styleChanges(items.value, [id], patch)),
  toneOf, obstacles, endRadius, labelLayer: computed(() => flow.viewportRef.value?.querySelector<HTMLElement>(".vue-flow__edge-labels") ?? undefined),
});
/** A node of the minimap in its item's colour (the tone classes below), a frame as a pale area. */
/** The minimap's tone of a colour: a palette colour of spaces (`indigo-4`) by its nearest board hue. */
const paletteHues: Record<string, string> = { amber: "yellow", violet: "purple", slate: "gray", lime: "green", cyan: "teal", indigo: "blue" };
const minimapTone = (tone: string) => { const hue = /^([a-z]+)-[1-5]$/.exec(tone)?.[1]; return hue ? paletteHues[hue] ?? hue : tone; };
function minimapClass(node: GraphNode) {
  const item = (node.data as { item?: BoardViewItem } | undefined)?.item;
  const tone = item && toneOf(item);
  return `board-minimap-node${item?.kind === "frame" ? " is-frame" : ""}${tone ? ` tone-${minimapTone(tone)}` : ""}`;
}
const surfaceLabel = computed(() => (props.ariaLabel ?? (props.canEdit
  ? t.canvas.surfaceEdit
  : t.canvas.surfaceRead)));
/** Brings a board point to the middle of the view (a collaborator's pointer, from the avatars). */
function centreOn(point: Point) {
  touched = true;
  const box = rect(), v = viewport.value;
  travelTo({ x: box.width / 2 - point.x * v.zoom, y: box.height / 2 - point.y * v.zoom, zoom: v.zoom });
}
/**
 * The selection toolbar comes and goes by Web Animations: a CSS transition makes Vue force a reflow of the whole board when it leaves
 * (79 ms of switching the lock on a 450-item board). Its times and easing are read once, not at each animation.
 */
let toolbarMotion: { menu: number; surface: number; ease: string } | undefined;
const toolbarTimes = (el: Element) => (toolbarMotion ??= { menu: motionMs(el, "--atlas-motion-menu"), surface: motionMs(el, "--atlas-motion-surface"), ease: motionEasing(el, "--atlas-ease-out") });
function toolbarEnter(el: Element, done: () => void) {
  const { menu, ease } = toolbarTimes(el);
  const animation = animateAtlas(el, [{ opacity: 0, scale: ".98" }, { opacity: 1, scale: "1" }], { duration: menu, easing: ease });
  if (animation) animation.finished.then(done, done); else done();
}
function toolbarLeave(el: Element, done: () => void) {
  const { surface, ease } = toolbarTimes(el);
  const animation = animateAtlas(el, [{ opacity: 1 }, { opacity: 0 }], { duration: surface, easing: ease, fill: "forwards" });
  if (animation) animation.finished.then(done, done); else done();
}
/** Back to the selected item (a panel over the board closed), else to the board (J6.3: back to what opened it). */
function focusSelection() { if (single.value) focusItem(single.value); else focusSurface(); }
defineExpose({ fit, zoomTo, centreOn, focusSurface, focusSelection });
</script>

<template>
  <section class="board-canvas" :data-highlighting="highlight ? '' : undefined" :class="{ 'is-readonly': !canEdit, 'is-panning': panning, 'is-connecting': connecting, 'is-dragging': dragging, 'is-refocused': fading }" :aria-label="t.canvas.board(label)"
    data-board-canvas :data-tool="activeTool">
    <div ref="surface" class="board-surface" tabindex="0" :aria-label="surfaceLabel" data-board-surface data-focus-landing :data-board-selection="selected.size ? '' : undefined"
      :style="{ '--board-edge-z': edgeLevel }"
      @keydown="onKeydown" @keyup="onKeyup" @focusin="takeTypedAhead($event); focusedEdge = ($event.target as Element).closest?.('.vue-flow__edge')?.getAttribute('data-id') ?? undefined" @focusout="focusedEdge = undefined" @wheel="onWheel" @pointermove="onPointerMove" @pointerleave="onPointerLeave" @dblclick="onDoubleClick" @contextmenu="onContextMenu"
      @copy="onCopy" @cut="onCopy($event, true)" @paste="onPaste" @dragover="onDragOver" @drop="onDrop" @touchstart.capture="onTouchStart">
      <BoardBackground :viewport="viewport" :settings="settings" :width="size.width" />
      <VueFlow :id="flowId" class="board-flow" :node-types="nodeTypes" :edge-types="edgeTypes" :apply-default="false"
        :min-zoom="0.05" :max-zoom="4" :zoom-on-scroll="false" :zoom-on-pinch="false" :pan-on-scroll="false" :zoom-on-double-click="false"
        :pan-on-drag="panning || touching ? true : MIDDLE_BUTTON" :selection-key-code="activeTool === 'select' && !panning && !touching ? true : null" multi-selection-key-code="Shift"
        :delete-key-code="null" :disable-keyboard-a11y="true" :elevate-nodes-on-select="false" :elevate-edges-on-select="false"
        :nodes-draggable="canEdit && activeTool === 'select'" :nodes-connectable="true" :edges-updatable="true" :elements-selectable="activeTool !== 'hand'"
        :connection-mode="ConnectionMode.Loose" :connection-radius="28" :edge-updater-radius="endRadius" :selection-mode="SelectionMode.Full"
        :node-drag-threshold="2" :only-render-visible-elements="virtualise" :auto-pan-on-node-drag="true" :auto-pan-on-connect="true"
        @nodes-change="onNodesChange" @edges-change="onEdgesChange" @node-drag-start="onDragStart" @node-drag-stop="onDragStop"
        @connect-start="onConnectStart" @connect="onConnect" @connect-end="onConnectEnd"
        @edge-update-start="onEdgeUpdateStart" @edge-update="onEdgeUpdate" @edge-update-end="onEdgeUpdateEnd"
        @node-double-click="onNodeDoubleClick" @edge-double-click="onEdgeDoubleClick" @pane-click="onPaneClick" @pane-ready="onPaneReady"
        @node-click="onNodeClick" @edge-click="keepKeys"
        @node-mouse-enter="hoverNode($event.node.id)" @node-mouse-leave="hoverNode()"
        @edge-mouse-enter="hoveredEdge = $event.edge.id" @edge-mouse-leave="hoveredEdge = undefined">
        <template #connection-line="line">
          <BoardConnectorPreview v-if="connectionPreview(line)" v-bind="connectionPreview(line)!" />
        </template>
        <MiniMap v-if="minimap" class="board-minimap" pannable zoomable :node-class-name="minimapClass" :width="180" :height="120" :aria-label="t.canvas.minimap" />
      </VueFlow>
      <BoardGuides :guides="guides" :viewport="viewport" />
      <svg v-if="linkPreview" class="board-link-preview" aria-hidden="true">
        <g :transform="`translate(${viewport.x} ${viewport.y}) scale(${viewport.zoom})`"><BoardConnectorPreview v-bind="linkPreview" /></g>
      </svg>
      <BoardPresence :remote="remote" :viewport="viewport" :box-of="presenceBox" />
      <span class="sr-only" aria-live="polite" data-board-announcement>{{ announcement }}</span>
      <span ref="menuTheme" class="md-editor atlas-document-editor" :class="editorTheme" hidden />
    </div>
    <BoardToolRail class="board-canvas-rail" :orientation="compact ? 'horizontal' : 'vertical'" :tool="activeTool" :can-edit="canEdit" :can-undo="canUndo" :can-redo="canRedo" :can-pick-page="!!pickPage"
      :multi-select="touching ? multiTap : undefined" @multi-select="multiTap = !multiTap; focusSurface()"
      @tool="tool = $event; focusSurface()" @pick-page="pickPages" @components="openComponents" @undo="undo(); keepKeys()" @redo="redo(); keepKeys()" />
    <div v-if="inserting" class="board-canvas-inserting" data-board-inserting>
      <BoardBlock :block="null" :insert="inserting.module" editing :host="blockHost" @commit="inserted" @cancel="inserting = null" />
    </div>
    <div class="board-canvas-corner">
      <BoardSettingsPopover v-model:open="settingsOpen" class="board-canvas-settings" :settings="settings" :can-edit="canEdit" @change="changeSettings" @go-to="goToDate" />
      <BoardZoomControls :zoom="viewport.zoom" :minimap="minimap" :has-selection="selected.size > 0"
        @step="stepZoom" @zoom-to="zoomTo" @fit="fit($event, 4)" @minimap="minimap = !minimap" />
    </div>
    <Transition :css="false" @enter="toolbarEnter" @leave="toolbarLeave">
      <div v-if="toolbarAt" :ref="measureToolbar" class="board-canvas-toolbar" :class="{ 'is-below': !toolbarAt.above }" :style="{ transform: `translate(${toolbarAt.x}px, ${toolbarAt.y}px)` }"
        @keydown.esc.exact.prevent="backToSelection" @keydown="onToolbarKeydown">
        <!-- An action that changes the selection takes its button away: the focus goes on with the selection, not to the page (BD-4). -->
        <BoardContextToolbar :items="selectedItems" :can-edit="canEdit" :arrangeable="canArrange" @layout="layOut" @style="restyle" @reorder="reorder" @duplicate="duplicate" @remove="removeSelection"
          @open="selectedItems[0] && open(selectedItems[0])" @edit-text="single && startEdit(single)" @align="align" @distribute="distribute"
          @group="group(); backToSelection()" @ungroup="ungroup(); backToSelection()" @collapse="setCollapsed" @detail="single && emit('openDetail', single)"
          @select-members="selectMembers(); backToSelection()" />
      </div>
    </Transition>
    <Transition name="board-command">
      <BoardCommandPanel v-if="panel" :commands="commands" :finds="finds" :at="panel.at" :room="size" :typed="panelTyped" :heading="panel.components ? t.canvas.components : undefined" @run="runCommand" @close="closePanel" />
    </Transition>
  </section>
</template>

<style scoped>
/* A board read only keeps its connectors' ends mounted (switching the lock remounts nothing) but never offers them. */
.board-canvas.is-readonly :deep(.vue-flow__edgeupdater) { display: none; }
/* How far the rest fades under the neighbour highlighting and a filter (U4-12): dark mode needs more of it to stay legible. */
.board-canvas { --board-dim-node: .3; --board-dim-edge: .18; }
:global([data-mode="dark"]) .board-canvas { --board-dim-node: .5; --board-dim-edge: .4; }
.board-canvas { --board-surface: var(--canvas, var(--surface)); container: board-canvas / inline-size; position: relative; min-width: 0; height: 100%; min-height: 420px; overflow: hidden;
  border: 1px solid var(--line); border-radius: var(--atlas-radius-lg); background: var(--board-surface); }
.board-surface { position: absolute; inset: 0; outline: none; touch-action: none; }
.board-surface:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
.board-flow { position: absolute; inset: 0; }
.board-canvas.is-refocused .board-flow :deep(.vue-flow__transformationpane) { animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
.board-canvas-rail { position: absolute; top: 12px; left: 12px; z-index: 6; }
.board-canvas-inserting { position: absolute; top: 50%; left: 50%; z-index: 7; width: min(360px, calc(100cqi - 32px)); translate: -50% -50%;
  border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--surface); box-shadow: var(--refresh-card-shadow-hover); animation: st-fade-in var(--atlas-motion-menu) var(--atlas-ease-out); }
.board-canvas-corner { position: absolute; right: 12px; bottom: 12px; z-index: 6; display: flex; align-items: center; gap: 6px; }
.board-canvas-settings { border: 1px solid var(--line); background: var(--surface); box-shadow: var(--refresh-card-shadow); }
.board-canvas-toolbar { position: absolute; top: 0; left: 0; z-index: 7; max-width: calc(100cqi - 16px); translate: -50% -100%; transition: transform var(--atlas-motion-base) var(--atlas-ease-travel); }
.board-canvas-toolbar :deep(.board-toolbar) { flex-wrap: wrap; }
.board-canvas-toolbar.is-below { translate: -50% 0; }
.board-command-leave-active { animation: st-fade-out var(--atlas-motion-surface) var(--atlas-ease-out) both; }
.board-minimap { margin: 0 12px 64px 0; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--surface); box-shadow: var(--refresh-card-shadow); }
.board-canvas[data-tool="hand"] .board-flow :deep(.vue-flow__pane), .board-canvas.is-panning .board-flow :deep(.vue-flow__pane) { cursor: grab; }
.board-canvas:is([data-tool="note"], [data-tool="text"], [data-tool="shape"], [data-tool="frame"]) .board-flow :deep(.vue-flow__pane) { cursor: crosshair; }
.board-flow :deep(.vue-flow__selection) { border: 1px solid var(--accent); border-radius: 2px; background: color-mix(in oklab, var(--accent) 8%, transparent); animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
.board-link-preview { position: absolute; inset: 0; width: 100%; height: 100%; overflow: visible; pointer-events: none; }
/* Connector labels in the edges' own layer, after them: above their lines (a frame lifts that layer), below the items. */
.board-flow :deep(.vue-flow__edge-labels) { z-index: var(--board-edge-z, 0); }
.board-flow :deep(.vue-flow__node:focus-visible) { outline: var(--focus-ring); outline-offset: var(--focus-offset); border-radius: var(--atlas-radius-md); }
.board-flow :deep(.board-minimap-node) { fill: color-mix(in oklab, var(--muted) 35%, var(--surface)); }
/* The board colours' hues (boardPalette → styles/appearance.css), strong enough to read at the minimap's size. */
.board-flow :deep(.board-minimap-node.tone-yellow) { --ah: 80; --ac: .15; }
.board-flow :deep(.board-minimap-node.tone-orange) { --ah: 55; --ac: .16; }
.board-flow :deep(.board-minimap-node.tone-red) { --ah: 27; --ac: .17; }
.board-flow :deep(.board-minimap-node.tone-pink) { --ah: 352; --ac: .16; }
.board-flow :deep(.board-minimap-node.tone-purple) { --ah: 302; --ac: .16; }
.board-flow :deep(.board-minimap-node.tone-blue) { --ah: 255; --ac: .15; }
.board-flow :deep(.board-minimap-node.tone-teal) { --ah: 182; --ac: .11; }
.board-flow :deep(.board-minimap-node.tone-green) { --ah: 152; --ac: .15; }
.board-flow :deep(.board-minimap-node.tone-gray) { --ah: 255; --ac: .025; }
.board-flow :deep(.board-minimap-node[class*="tone-"]:not(.tone-white, .tone-black)) { fill: oklch(72% var(--ac) var(--ah)); }
.board-flow :deep(.board-minimap-node.tone-black) { fill: var(--text); }
.board-flow :deep(.board-minimap-node.is-frame) { fill: color-mix(in oklab, var(--muted) 12%, var(--surface)); }
.board-flow :deep(.board-minimap-node.is-frame[class*="tone-"]:not(.tone-white, .tone-black)) { fill: oklch(72% var(--ac) var(--ah) / .25); }
@container board-canvas (max-width: 520px) { .board-canvas-rail { top: 8px; left: 8px; } .board-canvas-corner { right: 8px; bottom: 8px; } }
/* COMPACT_WIDTH: the rail is a bar along the bottom (thumb's reach), scrolling sideways when it does not fit; the zoom goes up. */
@container board-canvas (max-width: 480px) {
  .board-canvas-rail { top: auto; bottom: 8px; left: 50%; max-width: calc(100cqi - 16px); translate: -50% 0; }
  .board-canvas-corner { top: 8px; bottom: auto; }
}
</style>
