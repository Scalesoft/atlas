<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from "vue";
import { opensActions } from "../keyboard/navigation";
import ActionMenu, { type ActionMenuItem } from "./ActionMenu.vue";

/**
 * „Co s tím jde dělat“ for a row of a list (P2): the same editor-look menu as the page tree and the board (ActionMenu), opened by the same
 * five gestures: Ctrl+mezerník, Alt+Enter, Ctrl+., Shift+F10 / the menu key, and the right button (Shift + right button leaves the browser's).
 * Put it inside the row (the closest `li` or `[data-row-actions]`); it listens on that row and offers the rows its owner would run from the
 * row's own buttons, so a row without items (a locked one, a read-only list) stays as the browser has it. The focus goes back where it was.
 * Typing fields inside the row keep their keys. Use it for a list of rows; the tree keeps its own host (one menu for many rows with drag).
 */
const props = defineProps<{ items: readonly ActionMenuItem[]; label: string }>();
const emit = defineEmits<{ select: [id: string] }>();

const host = ref<HTMLElement>();
const open = ref(false);
const at = ref({ left: "0px", top: "0px" });
let row: HTMLElement | null | undefined, returnTo: HTMLElement | null = null;

const inField = (target: EventTarget | null) => target instanceof Element && !!target.closest("input, textarea, select, [contenteditable='true']");
function show(x: number, y: number, from: EventTarget | null) {
  at.value = { left: `${x}px`, top: `${y}px` };
  returnTo = from instanceof HTMLElement && row?.contains(from) ? from : row?.querySelector<HTMLElement>("button:not(:disabled), a[href]") ?? null;
  open.value = true;
}
function onKeydown(event: KeyboardEvent) {
  if (!props.items.length || event.defaultPrevented || !opensActions(event) || inField(event.target)) return;
  event.preventDefault();
  const box = (event.target instanceof Element ? event.target : row)!.getBoundingClientRect();
  show(box.left, box.bottom, event.target);
}
function onContext(event: MouseEvent) {
  if (!props.items.length || event.defaultPrevented || event.shiftKey || inField(event.target)) return;
  event.preventDefault();
  // The menu key fires it at 0,0 on the focused element: it then opens under that element.
  if (event.clientX || event.clientY) show(event.clientX, event.clientY, event.target);
  else { const box = (event.target as Element).getBoundingClientRect(); show(box.left, box.bottom, event.target); }
}
onMounted(() => {
  row = host.value?.closest<HTMLElement>("[data-row-actions], li");
  row?.addEventListener("keydown", onKeydown);
  row?.addEventListener("contextmenu", onContext);
});
onBeforeUnmount(() => { row?.removeEventListener("keydown", onKeydown); row?.removeEventListener("contextmenu", onContext); row = null; returnTo = null; });

/** The menu returns the focus to the element it was opened from, unless the chosen action has already put it somewhere (a dialog, the next row). */
function closed(event: Event) {
  event.preventDefault();
  const active = document.activeElement, target = returnTo;
  returnTo = null;
  if (target?.isConnected && (!active || active === document.body || active.closest(".board-menu"))) target.focus({ preventScroll: true });
}
</script>

<template>
  <span ref="host" class="row-actions-host" aria-hidden="true">
    <ActionMenu v-model:open="open" actions :items="items" :label="label" item-attr="data-row-action" side="bottom" align="start" @select="emit('select', $event)" @close-auto-focus="closed">
      <span class="row-actions-anchor" :style="at" />
    </ActionMenu>
  </span>
</template>

<style scoped>
.row-actions-host { position: absolute; width: 0; height: 0; overflow: visible; pointer-events: none; }
/* Placed at the pointer or under the focused element in viewport coordinates, wherever the row scrolls. */
.row-actions-anchor { position: fixed; width: 0; height: 0; pointer-events: none; }
</style>
