<script lang="ts">
import { typeAheadBuffer } from "../keyboard/navigation";
const sharedTypeAhead = typeAheadBuffer();
</script>
<script setup lang="ts">
import componentsTree from "../i18n/components-tree";
import { opensActions, typeAheadIndex } from "../keyboard/navigation";
import { computed, inject, nextTick, onBeforeUnmount, onMounted, provide, ref, watch, type ComponentPublicInstance } from "vue";
import { contentItemLink, isPlainNavigationClick } from "../pageLinks";
import AtlasIcon from "./AtlasIcon.vue";
import PageIcon from './PageIcon.vue';
import { isPageIcon } from '../pageDecorations';
import { PAGE_TREE, type PageTreeContext } from "./pageTreeContext";
import { animateAtlas, riseIn, RISE_IN_ROWS, travelTiming } from "../motion/aiMotion";
import { truncatedTitle } from "../lib/truncatedTitle";
import { visibleHeight } from "../lib/visibleHeight";
import { carriesReference, readReferenceDrop, startObjectDrag, type ReferenceDrop } from "../boards/boardDrop";
import { isBoardNode, isLeafNode, isPageNode, isReferenceNode, resolveTreeInsert, resolveTreeMove, visibleTreeOrder, type DropPosition, type TreeDrop, type TreePage } from "../treeMove";
import type { ActionMenuItem } from "./ActionMenu.vue";
import { closeRowActionsMenu, openRowActionsMenu, preheatActionsMenu, rowActionsMenuOpen } from "./boards/canvas/actionsMenuChunk";
import { hintLabel } from "../keyboard/shortcuts";
import SearchHighlight from "./SearchHighlight.vue";
import { plainTitle, typeInfo } from "../objects/relationsApi";
import { useToast } from "../composables/useToast";

/** What the row menu (spec 2026-10-27 §5.3) asks the app to do with a node; renaming happens here, in place. */
export type TreeAction = "open" | "openAttached" | "copyLink" | "newPage" | "newBoard" | "move" | "sortByTitle" | "trash" | "remove";
const props = withDefaults(
  defineProps<{
    pages: TreePage[];
    parentId?: string | null;
    activeId?: string;
    depth?: number;
    draggedId?: string;
    busy?: boolean;
    canReorder?: boolean;
    collapsedIds?: ReadonlySet<string>;
    /** Rows get the „⋯“ menu (also Ctrl+Space, Alt+Enter, Ctrl+., Shift+F10 and the right button), F2 or a double click to rename, and Delete. */
    menu?: boolean;
    /** The menu offers „Nová tabule zde“ (module Boards). */
    boards?: boolean;
    /** A letter typed on a row opens the tree's filter (`filter`) instead of jumping to the next row starting with it. */
    filterable?: boolean;
    /** Rows the filter folds away (treeFilter.ts), the query its matches highlight and the preselected match. */
    hiddenIds?: ReadonlySet<string>;
    highlight?: string;
    selectedId?: string;
    /** Rows can be dragged („Přesouvat stránky tažením“ of the profile); off, the menu's „Přesunout…“ and Alt+arrows still move. */
    drag?: boolean;
  }>(),
  { parentId: null, depth: 0, collapsedIds: () => new Set<string>(), hiddenIds: () => new Set<string>(), highlight: "", selectedId: undefined, drag: true },
);
const emit = defineEmits<{
  select: [page: TreePage]; dragStart: [page: TreePage, event: DragEvent]; dragEnd: []; drop: [destination: TreeDrop]; toggleCollapsed: [id: string];
  /** Something dropped from outside the tree (an object, a section, a page of another tree): a reference node goes there. */
  insert: [destination: TreeDrop, target: ReferenceDrop];
  action: [action: TreeAction, page: TreePage];
  rename: [page: TreePage, title: string];
  filter: [typed: string];
}>();
const dropHint = ref<{ id: string; position: DropPosition }>();

// The component renders itself for children, but every level has to agree on the
// single item carrying tabindex 0 and on the order arrow keys walk. The root derives
// them once and the nested instances read them (see pageTreeContext.ts). Threading
// them as props would push tree-internal state out to App.vue for no reason.
function rootContext(): PageTreeContext {
  const focusedId = ref<string | undefined>();
  const order = computed(() => visibleTreeOrder(props.hiddenIds.size ? props.pages.filter(page => !props.hiddenIds.has(page.id)) : props.pages, props.collapsedIds));
  const visible = computed(() => new Set(order.value.map(page => page.id)));
  const children = computed(() => {
    const index = new Map<string | null, TreePage[]>();
    for (const page of props.pages) {
      const siblings = index.get(page.parentId) ?? [];
      siblings.push(page); index.set(page.parentId, siblings);
    }
    return index;
  });
  // Until the reader touches the tree, the tab stop is the active page, else the
  // first one: tabbing in should land where they already are.
  // A remembered id can stop being visible without the reader touching the tree:
  // collapsing an ancestor, or the page simply leaving the list. If the tab stop
  // still pointed at it, no rendered row would carry tabindex 0 and the whole tree
  // would drop out of the tab order.
  const tabStopId = computed(() => {
    const remembered = focusedId.value;
    if (remembered && visible.value.has(remembered)) return remembered;
    if (props.activeId && visible.value.has(props.activeId)) return props.activeId;
    return order.value[0]?.id;
  });
  // One icon column for the whole tree, so titles start in one line at every level.
  // A board always shows its icon, so a tree with a board keeps the column too.
  const iconColumn = computed(() => props.pages.some(page => isLeafNode(page) || isPageIcon(page.decoration?.icon)));
  return { focusedId, order, children, tabStopId, iconColumn, menuFor: ref(), renamingId: ref(), notice: ref(""), refusedDrop: { at: 0 } };
}
const inherited = inject(PAGE_TREE, undefined);
const tree = inherited ?? rootContext();
if (!inherited) provide(PAGE_TREE, tree);
const { focusedId, order, tabStopId, children: indexedChildren, iconColumn, menuFor, renamingId, notice } = tree;

// One buffer for every nested list of the tree: a single item has the keyboard at a time.
const typeAhead = sharedTypeAhead;
function focusItem(id: string | undefined) {
  if (!id) return;
  focusedId.value = id;
  // The element may live in a sibling instance, so reach for it by attribute
  // rather than through a template ref.
  const selector = `.tree-page[data-tree-item="${CSS.escape(id)}"]`;
  // The row is there (arrow keys) or the next render draws it (the link that stands in for a rename's field again): no frame is waited for.
  const row = document.querySelector<HTMLElement>(selector);
  if (row) row.focus(); else void nextTick(() => document.querySelector<HTMLElement>(selector)?.focus());
}

function step(page: TreePage, delta: number) {
  const index = order.value.findIndex(item => item.id === page.id);
  if (index < 0) return;
  const next = order.value[index + delta];
  if (next) focusItem(next.id);
}

function keyboardMove(page: TreePage, position: DropPosition, targetId: string | null) {
  if (!props.canReorder || props.busy) return false;
  const drop: TreeDrop = { targetId, position, sourceId: page.id };
  // The same gate a mouse drop passes (dropOn), so canEdit and self-move rules
  // apply to the keyboard for free.
  if (!resolveTreeMove(props.pages, page.id, drop)) return false;
  emit("drop", drop);
  return true;
}
/** A refused nesting is said, not left silent: the status line below the tree (re-set so the same words are heard again). */
function say(message: string) {
  notice.value = "";
  void nextTick(() => { notice.value = message; });
}

function reorder(page: TreePage, delta: number) {
  const siblings = order.value.filter(item => item.parentId === page.parentId);
  const index = siblings.findIndex(item => item.id === page.id);
  const neighbour = siblings[index + delta];
  if (!neighbour) return;
  keyboardMove(page, delta < 0 ? "before" : "after", neighbour.id);
}

function keys(page: TreePage, event: KeyboardEvent) {
  const hasChildren = indexedChildren.value.has(page.id);
  const collapsed = props.collapsedIds.has(page.id);
  if (props.menu && rowKeys(page, event)) { event.preventDefault(); return; }
  // Ctrl/⌘+Shift+↑/↓ moves the page among its siblings as in the editor; Alt+↑/↓ below stays (Alt+↑/↓ folds in the editor).
  if ((event.ctrlKey || event.metaKey) && event.shiftKey && !event.altKey && (event.key === "ArrowUp" || event.key === "ArrowDown")) { event.preventDefault(); reorder(page, event.key === "ArrowUp" ? -1 : 1); return; }
  if (event.altKey) {
    if (event.ctrlKey || event.metaKey || event.shiftKey || !["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"].includes(event.key)) return;
    event.preventDefault();
    if (event.key === "ArrowLeft") { if (page.parentId) keyboardMove(page, "after", page.parentId); } // out of its parent, right below it
    else if (event.key === "ArrowRight") {
      const siblings = order.value.filter(item => item.parentId === page.parentId);
      const above = siblings[siblings.findIndex(item => item.id === page.id) - 1];
      // under the sibling above, as its last child; a leaf (board, reference) or no sibling above takes nothing
      if (!(above && keyboardMove(page, "inside", above.id)) && props.canReorder && !props.busy) say(componentsTree.tree.noNest);
    } else reorder(page, event.key === "ArrowUp" ? -1 : 1);
    return;
  }
  switch (event.key) {
    case "ArrowDown": event.preventDefault(); step(page, 1); return;
    case "ArrowUp": event.preventDefault(); step(page, -1); return;
    case "Home": event.preventDefault(); focusItem(order.value[0]?.id); return;
    case "End": event.preventDefault(); focusItem(order.value[order.value.length - 1]?.id); return;
    case "PageDown": case "PageUp": { // ten rows, not the browser's scroll that leaves the focus behind (SE5-3)
      event.preventDefault();
      const at = order.value.findIndex(item => item.id === page.id) + (event.key === "PageDown" ? 10 : -10);
      focusItem(order.value[Math.min(order.value.length - 1, Math.max(0, at))]?.id);
      return;
    }
    case "ArrowRight":
      event.preventDefault();
      if (hasChildren && collapsed) toggle(page.id);
      else if (hasChildren) step(page, 1);
      return;
    case "ArrowLeft":
      event.preventDefault();
      if (hasChildren && !collapsed) toggle(page.id);
      else if (page.parentId) focusItem(page.parentId);
      return;
    default: {
      // In the sidebar a letter opens the tree's filter with it (spec §5.2: type-ahead becomes a filter).
      if (props.filterable && event.key.length === 1 && event.key.trim() && !event.ctrlKey && !event.metaKey && !event.isComposing) {
        event.preventDefault(); emit("filter", event.key); return;
      }
      // Type-ahead (keyboard pass): letters jump to the next visible page whose title starts with them.
      const typed = typeAhead(event);
      if (!typed) return;
      const index = typeAheadIndex(order.value.map(item => plainTitle(item.title)), order.value.findIndex(item => item.id === page.id), typed);
      if (index < 0) return;
      event.preventDefault();
      focusItem(order.value[index]!.id);
    }
  }
}
watch(() => props.draggedId, () => { dropHint.value = undefined; });
function startDrag(page: TreePage, event: DragEvent) {
  // „Přesouvat stránky tažením“ off: the row is not draggable, but its link is natively, and that drag bubbles here.
  if (!props.drag) { event.preventDefault(); return; }
  // Any readable page can be dragged out as a copy (a board makes a card of it); only a reorderable one also starts the tree's own move.
  const reorder = props.canReorder && !props.busy && page.canEdit;
  if (reorder) {
    emit("dragStart", page, event);
    if (event.defaultPrevented) return;
  }
  // A board or a reference is no page: only its object payload, which a board canvas places as the target's card and another
  // space's tree as a reference to it; this tree moves it.
  if (isLeafNode(page)) {
    const object = isBoardNode(page) ? { id: page.id, typeId: "atlas.board", title: page.title }
      : page.targetId && page.targetTypeId ? { id: page.targetId, typeId: page.targetTypeId, title: page.title, ...(page.anchor && { anchor: page.anchor }) } : undefined;
    if (!object && !reorder) { event.preventDefault(); return; }
    if (object) startObjectDrag(event, object, { effect: reorder ? "copyMove" : "copy", bare: true });
    if (event.dataTransfer) event.dataTransfer.effectAllowed = reorder ? "copyMove" : "copy";
    return;
  }
  event.dataTransfer?.setData("application/x-atlas-page", page.id);
  if (event.dataTransfer) event.dataTransfer.effectAllowed = reorder ? "copyMove" : "copy";
}
/** A drag let go over a row it may not go to for want of the right (dragover repeats while it hovers): a brief reason, not silence. */
function endDrag(event: DragEvent) {
  dropHint.value = undefined;
  const { page, at } = tree.refusedDrop;
  tree.refusedDrop = { at: 0 };
  if (page && event.dataTransfer?.dropEffect === "none" && event.timeStamp - at < 400)
    useToast().error(componentsTree.tree.dropRefused(plain(page)));
  emit("dragEnd");
}
// "inside" reparents, which is the outcome that surprises people, so it gets the
// smallest share of the row rather than the largest: a third each way, not 25/50/25.
function destination(page: TreePage, event: DragEvent): TreeDrop {
  const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
  const relative = (event.clientY - box.top) / Math.max(box.height, 1);
  // A board or a reference is a leaf: its row is split between before and after.
  if (isLeafNode(page)) return { targetId: page.id, position: relative < .5 ? "before" : "after" };
  return { targetId: page.id, position: relative < .33 ? "before" : relative > .67 ? "after" : "inside" };
}
/** A drag from outside this tree (no row of it is dragged) that carries something a reference node can name. */
const fromOutside = (event: DragEvent) => !props.draggedId && !!event.dataTransfer && carriesReference(event.dataTransfer.types);
/** Where the drag would go, or nothing; the gate a move or an insert passes (resolveTreeMove). */
function accepted(drop: TreeDrop, event: DragEvent) {
  if (!props.canReorder || props.busy) return false;
  if (fromOutside(event)) return !!resolveTreeInsert(props.pages, drop);
  return !!props.draggedId && !!resolveTreeMove(props.pages, props.draggedId, drop);
}
function dragOver(page: TreePage, event: DragEvent) {
  const drop = destination(page, event);
  if (!accepted(drop, event)) {
    dropHint.value = undefined;
    if (props.draggedId && !page.canEdit && page.id !== props.draggedId) tree.refusedDrop = { page, at: event.timeStamp };
    return;
  }
  tree.refusedDrop = { at: 0 };
  event.preventDefault();
  if (event.dataTransfer) event.dataTransfer.dropEffect = props.draggedId ? "move" : "copy";
  dropHint.value = { id: page.id, position: drop.position };
}
function dropOn(page: TreePage, event: DragEvent) {
  const drop = destination(page, event);
  dropHint.value = undefined;
  if (!accepted(drop, event)) return;
  if (!props.draggedId) {
    const target = readReferenceDrop(event.dataTransfer!);
    if (!target) return;
    event.preventDefault(); emit("insert", drop, target);
    return;
  }
  event.preventDefault(); emit("drop", drop);
}

// The row menu (spec §5.3): the page editor's own menu (openRowActionsMenu, as list rows and the board; typing filters it), opened from
// a row's „⋯“, the right button, Ctrl+Space, Alt+Enter, Ctrl+., Shift+F10 or the menu key; F2 or a double click renames in place,
// Delete trashes (or removes a reference), Ctrl/⌘+Enter opens the page's own board.
const referenceIcon = (page: TreePage) => page.readable === false ? "lock" : typeInfo(page.targetTypeId ?? "").icon;
const plain = (page: TreePage) => plainTitle(page.title);
const titleOf = (page: TreePage) => page.readable === false ? componentsTree.tree.noAccessLink : plain(page);
function openMenu(page: TreePage, reference: Element | { getBoundingClientRect(): DOMRect }) {
  const items = menuItems(page);
  const host = rowElement(page) ?? list.value;
  if (props.busy || !items.length || !host) return;
  focusedId.value = page.id;
  menuFor.value = { page, reference };
  // The focus stays on the row while the menu is open and goes back to it after (renaming then takes it to its field).
  openRowActionsMenu({ items, label: componentsTree.tree.actionsOf(titleOf(page)), anchor: () => reference.getBoundingClientRect(), host,
    select: id => (id === "rename" ? startRename(page) : emit("action", id as TreeAction, page)),
    onClose: () => { if (menuFor.value?.page.id === page.id) menuFor.value = undefined; focusItem(page.id); } });
}
function rowElement(page: TreePage) {
  return document.querySelector<HTMLElement>(`.tree-row[data-tree-row="${CSS.escape(page.id)}"]`);
}
function contextMenu(page: TreePage, event: MouseEvent) {
  if (!props.menu || event.shiftKey) return; // Shift + right-click: the browser's own menu
  event.preventDefault();
  if (rowActionsMenuOpen()) return; // the menu key sends it after its keydown opened the menu
  // The menu key fires it at 0,0 on the focused row: it then opens at the row's „⋯“.
  const { clientX: x, clientY: y } = event;
  openMenu(page, x || y ? { getBoundingClientRect: () => new DOMRect(x, y, 0, 0) } : rowElement(page)?.querySelector(".tree-row-more") ?? event.currentTarget as Element);
}
function rowKeys(page: TreePage, event: KeyboardEvent) {
  const mod = event.ctrlKey || event.metaKey;
  if (opensActions(event)) { openMenu(page, rowElement(page)?.querySelector(".tree-row-more") ?? event.currentTarget as Element); return true; }
  if (event.key === "F2" && !mod && canRename(page)) { startRename(page); return true; }
  if (event.key === "Delete" && !mod && !event.shiftKey && page.canEdit) { emit("action", isReferenceNode(page) ? "remove" : "trash", page); return true; }
  if (event.key === "Enter" && mod && page.attachedBoardId) { emit("action", "openAttached", page); return true; }
  return false;
}
const canRename = (page: TreePage) => !!page.canEdit && isPageNode(page);
/** A row opens its page on the first click (as a board item is selected, D1), so the in-place gesture is the second one: F2, the row's menu, or this. */
function renameByDoubleClick(page: TreePage, event: MouseEvent) {
  if (!props.menu || props.busy || !canRename(page) || !(event.target instanceof Element) || event.target.closest("button, input")) return;
  event.preventDefault();
  startRename(page);
}
function menuItems(page: TreePage): ActionMenuItem[] {
  const edit = !!page.canEdit && props.canReorder, readable = page.readable !== false, isPage = isPageNode(page);
  const rows: (ActionMenuItem | false)[] = [
    readable && { id: "open", label: componentsTree.tree.open, icon: "arrow-right", group: "open" },
    !!page.attachedBoardId && { id: "openAttached", label: componentsTree.tree.openAttached, icon: "layout-dashboard", shortcut: hintLabel("Mod+Enter"), group: "open" },
    readable && !!contentItemLink(page) && { id: "copyLink", label: componentsTree.tree.copyLink, icon: "link", group: "open" },
    edit && isPage && { id: "newPage", label: componentsTree.tree.newSubpage, icon: "file-plus", group: "create" },
    edit && isPage && props.boards && { id: "newBoard", label: componentsTree.tree.newBoard, icon: "layout-dashboard", group: "create" },
    canRename(page) && { id: "rename", label: componentsTree.tree.rename, icon: "pencil", shortcut: "F2", group: "edit" },
    edit && !isReferenceNode(page) && { id: "move", label: componentsTree.tree.move, icon: "folder-input", group: "edit" },
    // Its direct children stay sorted by title until it is unticked or one of them is moved among them by hand.
    edit && isPage && { id: "sortByTitle", label: componentsTree.tree.sortByTitle, icon: "arrow-down-a-z", group: "edit", checked: !!page.childrenByTitle },
    edit && (isReferenceNode(page)
      ? { id: "remove", label: componentsTree.tree.removeFromTree, icon: "unlink", shortcut: "Delete", group: "trash" }
      : { id: "trash", label: componentsTree.tree.moveToTrash, icon: "trash", shortcut: "Delete", group: "trash", destructive: true }),
  ];
  return rows.filter((row): row is ActionMenuItem => !!row);
}
const renameDraft = ref("");
function startRename(page: TreePage) {
  renameDraft.value = page.title;
  renamingId.value = page.id;
  void nextTick(() => document.querySelector<HTMLInputElement>(`.tree-rename[data-tree-rename="${CSS.escape(page.id)}"]`)?.select());
}
/** `refocus` is false when the focus is on its way elsewhere (a click or Tab out of the field): focus() in that blur would steal it (P4). */
function finishRename(page: TreePage, save: boolean, refocus = true) {
  if (renamingId.value !== page.id) return;
  renamingId.value = undefined;
  const title = renameDraft.value.trim();
  if (save && title && title !== page.title) emit("rename", page, title);
  if (refocus) focusItem(page.id);
}

// A node that moved glides to its new place and a new one rises in (spec §5.5, FLIP on --atlas-ease-travel): only the root measures,
// before and after the list changes, and only when a few rows changed (a whole tree loading or replaced does not move).
const list = ref<HTMLElement>();
onMounted(() => { if (props.depth === 0 && props.menu) preheatActionsMenu(); });
onBeforeUnmount(() => { if (props.depth === 0 && menuFor.value) closeRowActionsMenu(); });
let tops = new Map<string, number>();
const rowTops = () => new Map([...(list.value?.querySelectorAll<HTMLElement>(".tree-row[data-tree-row]") ?? [])].map(row => [row.dataset.treeRow!, row.getBoundingClientRect().top]));
watch(() => props.pages, () => { if (props.depth === 0) tops = rowTops(); }, { flush: "pre" });
watch(() => props.pages, () => {
  if (props.depth !== 0 || !tops.size) return;
  const before = tops; tops = new Map();
  const rows = [...(list.value?.querySelectorAll<HTMLElement>(".tree-row[data-tree-row]") ?? [])];
  const now = new Set(rows.map(row => row.dataset.treeRow!));
  // A row replaced where it stood (an optimistic node by the server's) is the same row to the eye.
  const vacated = [...before].filter(([id]) => !now.has(id)).map(([, top]) => top);
  const arrived = rows.filter(row => !before.has(row.dataset.treeRow!) && !vacated.some(top => Math.abs(top - row.getBoundingClientRect().top) < 1));
  const moved = rows.map(row => ({ row, dy: (before.get(row.dataset.treeRow!) ?? NaN) - row.getBoundingClientRect().top })).filter(({ dy }) => Math.abs(dy) >= 1);
  if (arrived.length > 3 || moved.length > 60) return;
  for (const { row, dy } of moved) animateAtlas(row, [{ transform: `translateY(${dy}px)` }, { transform: "none" }], { ...travelTiming(row, dy), fill: "none" });
  riseIn(arrived, RISE_IN_ROWS);
}, { flush: "post" });
const children = computed(() => indexedChildren.value.get(props.parentId) ?? []);
/** The rows the filter leaves: their position and count are what a screen reader hears. */
const shownChildren = computed(() => props.hiddenIds.size ? children.value.filter(page => !props.hiddenIds.has(page.id)) : children.value);
// A branch toggled here (its chevron, the arrow keys on its row) folds and unfolds where the reader looks, so only its
// part in view has to move (lib/visibleHeight.ts). One revealed from elsewhere (a page opened in a folded branch)
// unfolds whole: the list follows the unfolding down to that page's row.
let asked: string | undefined;
function toggle(id: string) {
  asked = id;
  emit("toggleCollapsed", id);
  void nextTick(() => { asked = undefined; });
}
// A branch unfolds under its row and folds back into it (board 16 „Rozbalení“: height and opacity, the rows below
// move with it; reduced motion keeps the fade through animateAtlas). It leaves from rest and takes longer the further
// the rows below have to go (travelTiming), so a long branch glides instead of covering a third of its way in the
// first frame. A folding branch stays rendered until it has
// folded, so reversing it midway turns the same list around from the height it had reached. No <Transition>: the
// list stays the direct child of its row's item, and a tree of hundreds of rows pays nothing for it while it rests.
// Only a single toggled branch moves. „Sbalit vše“, „Rozbalit vše“ and revealing a page's ancestors change many
// at once and stay immediate: that many lists measured and animated together cost frames and explain nothing.
const closing = ref<ReadonlySet<string>>(new Set());
const branches = new Map<string, HTMLElement>();
const branchMotion = new Map<string, Animation>();
function setBranch(id: string, instance: unknown) {
  const el = (instance as ComponentPublicInstance | null)?.$el;
  if (el instanceof HTMLElement) branches.set(id, el); else branches.delete(id);
}
function closed(id: string) {
  if (!closing.value.has(id)) return;
  const next = new Set(closing.value);
  next.delete(id);
  closing.value = next;
}
function moveBranch(id: string, opening: boolean, limited = false) {
  const branch = branches.get(id);
  if (!branch) { closed(id); return; }
  const running = branchMotion.get(id);
  const turning = running?.playState === "running";
  // To the fraction of a pixel: turned around from a rounded height (offsetHeight), the rows below began with a step
  // of up to half a pixel.
  const from = turning ? { height: branch.getBoundingClientRect().height, opacity: getComputedStyle(branch).opacity } : undefined;
  running?.cancel();
  if (opening) { branch.removeAttribute("inert"); branch.removeAttribute("aria-hidden"); }
  else { branch.setAttribute("inert", ""); branch.setAttribute("aria-hidden", "true"); }
  const whole = branch.getBoundingClientRect().height, full = limited ? visibleHeight(branch, whole) : whole;
  const start = from?.height ?? (opening ? 0 : full), end = opening ? full : 0;
  const animation = animateAtlas(branch, [
    { height: `${start}px`, opacity: from?.opacity ?? (opening ? 0 : 1), overflow: "clip" },
    { height: `${end}px`, opacity: opening ? 1 : 0, overflow: "clip" },
  ], { ...travelTiming(branch, end - start, !opening), fill: opening ? "none" : "forwards" });
  if (!animation) { branchMotion.delete(id); if (!opening) closed(id); return; }
  branchMotion.set(id, animation);
  const settled = () => {
    if (branchMotion.get(id) !== animation) return;
    branchMotion.delete(id);
    if (!opening) closed(id);
  };
  animation.finished.then(settled, settled);
}
watch(() => props.collapsedIds, (now, before) => {
  let changed = 0;
  for (const id of now) if (!before.has(id)) changed += 1;
  for (const id of before) if (!now.has(id)) changed += 1;
  if (changed !== 1) { if (closing.value.size) closing.value = new Set(); return; }
  const toggled = children.value.find(page => now.has(page.id) !== before.has(page.id) && indexedChildren.value.has(page.id));
  if (!toggled) return;
  const limited = asked === toggled.id;
  if (now.has(toggled.id)) {
    closing.value = new Set(closing.value).add(toggled.id);
    moveBranch(toggled.id, false, limited);
  } else if (closing.value.has(toggled.id)) {
    closed(toggled.id);
    moveBranch(toggled.id, true, limited);
  } else void nextTick(() => moveBranch(toggled.id, true, limited));
});
// Chromium drops a native drag that starts inside a list clipped to an animated height, so a press on the tree ends
// the unfolding at once (as SidebarSpace does for the whole tree): a row can be dragged the moment it is visible.
function settleBranches() { for (const animation of [...branchMotion.values()]) animation.finish(); }
function select(page: TreePage, event: MouseEvent) {
  if (!isPlainNavigationClick(event)) return;
  event.preventDefault();
  // The second click of a double click (the rename gesture) finds the page already opened by the first: it is not fetched twice.
  if (event.detail > 1) return;
  emit("select", page);
}
// The row is as wide as the tree and lights up as a whole, so all of it opens the page: its indent and the empty
// arrow column of a page without children too, not only the link in it.
function selectRow(page: TreePage, event: MouseEvent) {
  if (!(event.target instanceof Element) || event.target.closest("a, button, input")) return;
  select(page, event);
}
</script>
<template>
  <ul
    ref="list"
    class="page-tree"
    :class="{ 'page-tree-nested': depth > 0 && depth <= 8 }"
    :role="depth === 0 ? 'tree' : 'group'"
    :style="depth > 0 ? { '--tree-depth': Math.min(depth, 8) } : undefined"
    @pointerdown.capture="settleBranches"
    @dragstart.capture="settleBranches"
  >
    <!-- The treeitem is the link, not the li, so a page stays a real anchor
         with a real href and keeps aria-current -- the APG navigation
         treeview shape. The li is presentational so the tree/group only
         ever contains treeitems. -->
    <li v-for="page in children" :key="page.id" role="none" :class="{ 'tree-filtered-out': hiddenIds.has(page.id) }" :inert="hiddenIds.has(page.id) || undefined"
      :aria-hidden="hiddenIds.has(page.id) || undefined">
      <div
        class="tree-row"
        :class="{ active: page.id === activeId, 'tree-branch': indexedChildren.has(page.id), 'tree-dragging': page.id === draggedId, [`drop-${dropHint?.position}`]: dropHint?.id === page.id,
                  'tree-filter-selected': page.id === selectedId, 'tree-row-menu-open': menuFor?.page.id === page.id }"
        :data-tree-row="page.id"
        :draggable="drag && page.readable !== false && !busy && renamingId !== page.id"
        @click="selectRow(page, $event)"
        @dblclick="renameByDoubleClick(page, $event)"
        @contextmenu="contextMenu(page, $event)"
        @dragstart.stop="startDrag(page, $event)"
        @dragend.stop="endDrag($event)"
        @dragover.stop="dragOver(page, $event)"
        @dragleave="dropHint = undefined"
        @drop.stop="dropOn(page, $event)"
      >
        <!-- Its hint is a native title (F32): a Tooltip per row would cost a long tree its mount time. -->
        <span class="tree-toggle-col">
          <button
            v-if="indexedChildren.has(page.id)"
            class="tree-toggle"
            tabindex="-1"
            :aria-expanded="!collapsedIds.has(page.id)"
            :aria-label="`${collapsedIds.has(page.id) ? componentsTree.tree.expand : componentsTree.tree.collapse} ${plain(page)}`"
            :title="collapsedIds.has(page.id) ? componentsTree.tree.expand : componentsTree.tree.collapse"
            @click="toggle(page.id)"
          >
            <AtlasIcon
              name="right"
              :size="14"
            />
          </button>
        </span>
        <!-- Renamed in place (F2): the field stands in for the row's link until Enter, Esc or leaving it. -->
        <input
          v-if="renamingId === page.id"
          v-model="renameDraft"
          class="tree-rename"
          :data-tree-rename="page.id"
          :aria-label="componentsTree.tree.newName(plain(page))"
          maxlength="200"
          @keydown.enter.prevent="finishRename(page, true)"
          @keydown.esc.prevent.stop="finishRename(page, false)"
          @blur="finishRename(page, true, !$event.relatedTarget)"
        >
        <a
          v-else
          class="tree-page"
          role="treeitem"
          :href="contentItemLink(page)"
          :data-tree-item="page.id"
          :data-tree-type="page.type ?? 'page'"
          :aria-current="page.id === activeId ? 'page' : undefined"
          :aria-level="depth + 1"
          :aria-posinset="shownChildren.indexOf(page) + 1"
          :aria-setsize="shownChildren.length"
          :aria-expanded="indexedChildren.has(page.id) ? !collapsedIds.has(page.id) : undefined"
          :tabindex="page.id === tabStopId ? 0 : -1"
          @pointerenter="truncatedTitle($event, '.tree-page-title')"
          @focus="focusedId = page.id"
          @click="select(page, $event)"
          @keydown="keys(page, $event)"
        >
          <!-- A board is told apart by its icon only (catalog §2), in the column page icons use; a reference by its target's icon
               with a small link mark, or a lock without a title when its target is not the reader's to read. -->
          <span v-if="isBoardNode(page)" class="tree-icon-slot tree-board-icon" aria-hidden="true" data-tree-board-icon><AtlasIcon name="layout-dashboard" :size="15" /></span>
          <span v-else-if="isReferenceNode(page)" class="tree-icon-slot tree-reference-icon" aria-hidden="true" data-tree-reference-icon>
            <AtlasIcon :name="referenceIcon(page)" :size="15" /><AtlasIcon v-if="page.readable !== false" class="tree-reference-mark" name="link" :size="9" /></span>
          <span v-else-if="iconColumn" class="tree-icon-slot" aria-hidden="true"><PageIcon :decoration="page.decoration" :size="16" /></span>
          <span v-if="page.readable === false" class="tree-page-title tree-reference-locked" aria-hidden="true" />
          <span v-else class="tree-page-title" :class="{ 'tree-page-draft': !page.isPublished }"><SearchHighlight v-if="highlight" :text="plain(page)" :query="highlight" /><template v-else>{{ plain(page) }}</template></span>
          <span v-if="!page.isPublished" class="tree-draft-dot" aria-hidden="true" />
          <!-- „Řadit abecedně“, quiet and for writers only: readers just see the order. A native title, as the toggle's (F32). -->
          <span v-if="page.childrenByTitle && canReorder" class="tree-sorted-mark" :title="componentsTree.tree.sortedTitle" data-tree-sorted>
            <AtlasIcon name="arrow-down-a-z" :size="12" /><span class="sr-only"> — {{ componentsTree.tree.sortedMark }}</span></span>
          <span v-if="isBoardNode(page)" class="sr-only"> — Tabule</span>
          <span v-if="isReferenceNode(page)" class="sr-only">{{ page.readable === false ? componentsTree.tree.referenceNoAccess : componentsTree.tree.referenceMark }}</span>
          <span v-if="page.attachedBoardId" class="sr-only"> — {{ componentsTree.tree.hasBoard }}</span>
          <span v-if="!page.isPublished" class="sr-only"> — Koncept</span>
        </a>
        <!-- The quiet mark of the page's own board: always there, so hover moves nothing; a click opens the board. -->
        <button v-if="page.attachedBoardId && renamingId !== page.id" type="button" class="tree-row-attached" tabindex="-1" data-tree-attached
          :aria-label="componentsTree.tree.openBoardOf(plain(page))" :title="componentsTree.tree.deploymentBoard" @click="emit('action', 'openAttached', page)">
          <AtlasIcon name="layout-dashboard" :size="13" /></button>
        <!-- Its room is reserved; it shows with hover and focus, on touch always. -->
        <button v-if="menu && renamingId !== page.id" type="button" class="tree-row-more" tabindex="-1" aria-haspopup="menu" data-tree-more
          :aria-label="componentsTree.tree.actionsOf(titleOf(page))" @click="openMenu(page, $event.currentTarget as Element)">
          <AtlasIcon name="more" :size="15" /></button>
      </div>
      <PageTree
        v-if="indexedChildren.has(page.id) && (!collapsedIds.has(page.id) || closing.has(page.id))"
        :ref="instance => setBranch(page.id, instance)"
        :pages="pages"
        :parent-id="page.id"
        :active-id="activeId"
        :depth="depth + 1"
        :dragged-id="draggedId"
        :busy="busy"
        :can-reorder="canReorder"
        :collapsed-ids="collapsedIds"
        :menu="menu"
        :boards="boards"
        :filterable="filterable"
        :hidden-ids="hiddenIds"
        :highlight="highlight"
        :selected-id="selectedId"
        :drag="drag"
        @select="emit('select', $event)"
        @drag-start="(page, event) => emit('dragStart', page, event)"
        @drag-end="emit('dragEnd')"
        @drop="emit('drop', $event)"
        @insert="(destination, target) => emit('insert', destination, target)"
        @action="(action, page) => emit('action', action, page)"
        @rename="(page, title) => emit('rename', page, title)"
        @filter="emit('filter', $event)"
        @toggle-collapsed="emit('toggleCollapsed', $event)"
      />
    </li>
    <li v-if="depth === 0" role="none" class="sr-only"><span role="status">{{ notice }}</span></li>
  </ul>
</template>

<style scoped>
/* A space can hold hundreds of pages: rows outside the scrolled view skip style, layout and paint, and keep their
   height (--tree-row-h is the row's min-height wherever the shell changes it). */
.tree-row { content-visibility: auto; contain-intrinsic-block-size: auto var(--tree-row-h, 32px); }
.tree-page { position: relative; overflow: hidden; cursor: pointer; text-decoration: none; }

.tree-page-title { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
/* Refresh 2.5: once any page of the tree carries an icon, every row keeps an icon column (empty where a page has
   none), so the titles start in one line. Undecorated pages get no decorative icon. */
.tree-icon-slot { display: inline-flex; align-items: center; justify-content: center; flex: 0 0 18px; width: 18px; height: 18px; }
/* A draft reads like any other title, a shade quieter, and carries a dot: after a
   Wiki.js import most of a tree can be drafts, and italics on every row made the
   whole tree harder to read while marking nothing apart. */
.tree-board-icon { color: var(--muted); }
.active .tree-board-icon { color: inherit; }
.tree-page-draft { color: color-mix(in srgb, var(--text) 72%, var(--muted)); }
.active .tree-page-draft { color: inherit; }
.tree-sorted-mark { display: inline-flex; flex: 0 0 auto; color: var(--muted); opacity: .75; }
.active .tree-sorted-mark { color: inherit; }
.tree-draft-dot { flex: 0 0 6px; width: 6px; height: 6px; margin-left: -2px; border-radius: 50%; background: var(--warning); opacity: .8; }
/* A reference node: its target's icon with a small link mark at its corner; an unreadable one a lock and no title. */
.tree-reference-icon { position: relative; color: var(--muted); }
.active .tree-reference-icon { color: inherit; }
.tree-reference-mark { position: absolute; right: -4px; bottom: -3px; padding: 1px; border-radius: 50%; background: var(--tree-row-bg, var(--surface)); color: var(--accent); box-sizing: content-box; }
/* The row's „⋯“ and the quiet mark of a page's own board keep their room; hover only shows the „⋯“ and colours them. */
.tree-row-more, .tree-row-attached {
  display: grid; place-items: center; flex: 0 0 24px; width: 24px; height: 24px; margin-right: 2px; padding: 0;
  border: 0; border-radius: 6px; background: transparent; color: var(--muted); cursor: pointer;
  transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out);
}
.tree-row-attached { flex-basis: 20px; width: 20px; margin-right: 0; opacity: .75; }
.tree-row-more { opacity: 0; }
.tree-row:focus-within .tree-row-more, .tree-row-menu-open .tree-row-more { opacity: 1; }
@media (hover: hover) and (pointer: fine) {
  .tree-row:hover .tree-row-more { opacity: 1; }
  .tree-row-more:hover, .tree-row-attached:hover { background: var(--bg-3, var(--hover)); color: var(--text); opacity: 1; }
}
@media (hover: none) { .tree-row-more { opacity: 1; } }
/* A touch screen (F6): the row's actions take 40 px across and the row's height as an invisible area, their 24 px look stays; it grows
   toward the title, the row's end clips it otherwise. */
@media (pointer: coarse) {
  .tree-row-more, .tree-row-attached { position: relative; }
  .tree-row-more::after, .tree-row-attached::after { content: ""; position: absolute; top: 50%; right: -2px; width: 40px; height: 40px; translate: 0 -50%; }
}
.tree-row-more:focus-visible, .tree-row-attached:focus-visible, .tree-rename:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
/* The filter's preselected match, the one Enter opens. */
.tree-row.tree-filter-selected { background: var(--state-active-row-bg); box-shadow: var(--state-active-row-mark); }
/* Rows the filter leaves out fold by their height and fade (interpolate-size where the browser has it; elsewhere they fade out). */
.page-tree { interpolate-size: allow-keywords; }
.page-tree > li { transition: height var(--atlas-motion-base) var(--atlas-ease-travel), opacity var(--atlas-motion-base) var(--atlas-ease-out); }
.page-tree > li.tree-filtered-out { height: 0; opacity: 0; overflow: clip; }
@media (prefers-reduced-motion: reduce) { .page-tree > li { transition: opacity var(--atlas-motion-surface) linear; } }
.tree-rename {
  flex: 1; min-width: 0; height: 26px; margin-right: 4px; padding: 0 6px; border: 1px solid var(--accent); border-radius: 6px;
  background: var(--surface); color: var(--text); font: inherit; font-size: 13.5px;
}
.page-tree[role="tree"] { position: relative; }
/* A draft says so by its dot (and to a screen reader in words). The word „Koncept“ that slid over the row on hover covered
   the end of the name it marked (review cycle 2, U2-16); hover never truncates a row differently. */
</style>
