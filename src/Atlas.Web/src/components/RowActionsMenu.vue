<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from "vue";
import { opensActions } from "../keyboard/navigation";
import type { ActionMenuItem } from "./ActionMenu.vue";
import { closeRowActionsMenu, openRowActionsMenu, preheatActionsMenu, rowActionsMenuOpen } from "./boards/canvas/actionsMenuChunk";

/**
 * „Co s tím jde dělat“ for a row of a list (P2): the page editor's own menu, as on a page, the tree and the board (openRowActionsMenu:
 * typing filters the rows), opened by the same five gestures: Ctrl+mezerník, Alt+Enter, Ctrl+., Shift+F10 / the menu key, and the right
 * button (Shift + right button leaves the browser's). Put it inside the row (the closest `li` or `[data-row-actions]`); it listens on that
 * row and offers the rows its owner would run from the row's own buttons, so a row without items (a locked one, a read-only list) stays as
 * the browser has it. The focus goes back where it was. Typing fields inside the row keep their keys. A row whose item names a key as its
 * `shortcut` („Delete“, „F2“) runs it on that key without the menu, as the tree and the board do. Use it for a list of rows; the tree keeps
 * its own host (one menu for many rows with drag). `label` names the menu („Akce: …“).
 */
const props = defineProps<{ items: readonly ActionMenuItem[]; label: string }>();
const emit = defineEmits<{ select: [id: string] }>();

const host = ref<HTMLElement>();
let row: HTMLElement | null | undefined, open = false;

const inField = (target: EventTarget | null) => target instanceof Element && !!target.closest("input, textarea, select, [contenteditable='true']");
const under = (element: Element) => () => { const box = element.getBoundingClientRect(); return new DOMRect(box.left, box.bottom, 0, 0); };
function show(anchor: () => DOMRect, from: EventTarget | null) {
  if (!host.value) return;
  const focusable = from instanceof Element ? from.closest<HTMLElement>("button:not(:disabled), a[href], [tabindex]") : null;
  const returnTo = focusable && row?.contains(focusable) ? focusable : row?.querySelector<HTMLElement>("button:not(:disabled), a[href]") ?? null;
  // The focus stays where it was while the menu is open; it goes back there unless the chosen action has already put it somewhere (a dialog, the next row).
  open = true;
  openRowActionsMenu({ items: props.items, label: props.label, anchor, host: host.value, select: id => emit("select", id), onClose: () => {
    open = false;
    const active = document.activeElement;
    if (returnTo?.isConnected && (!active || active === document.body)) returnTo.focus({ preventScroll: true });
  } });
}
function onKeydown(event: KeyboardEvent) {
  if (!props.items.length || event.defaultPrevented || inField(event.target)) return;
  const direct = (event.key === "Delete" || event.key === "F2") && !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey
    ? props.items.find(item => item.shortcut === event.key && !item.disabled) : undefined;
  if (direct) { event.preventDefault(); emit("select", direct.id); return; }
  if (!opensActions(event)) return;
  event.preventDefault();
  show(under(event.target instanceof Element ? event.target : row!), event.target);
}
function onContext(event: MouseEvent) {
  if (!props.items.length || event.defaultPrevented || event.shiftKey || inField(event.target)) return;
  event.preventDefault();
  // The menu key sends it after its keydown opened the menu: no second one.
  if (rowActionsMenuOpen()) return;
  // The menu key fires it at 0,0 on the focused element: it then opens under that element.
  const { clientX: x, clientY: y } = event;
  show(x || y ? () => new DOMRect(x, y, 0, 0) : under(event.target as Element), event.target);
}
onMounted(() => {
  row = host.value?.closest<HTMLElement>("[data-row-actions], li");
  row?.addEventListener("keydown", onKeydown);
  row?.addEventListener("contextmenu", onContext);
  preheatActionsMenu();
});
onBeforeUnmount(() => {
  row?.removeEventListener("keydown", onKeydown); row?.removeEventListener("contextmenu", onContext); row = null;
  if (open) closeRowActionsMenu(); // a row that goes away takes its menu along
});
</script>

<template>
  <span ref="host" class="row-actions-host" aria-hidden="true" />
</template>

<style scoped>
.row-actions-host { position: absolute; width: 0; height: 0; overflow: visible; pointer-events: none; }
</style>
