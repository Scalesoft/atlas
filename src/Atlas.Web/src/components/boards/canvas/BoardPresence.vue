<script setup lang="ts">
import { computed, defineComponent, h, onBeforeUnmount, ref, shallowRef, watch, watchEffect, type PropType, type VNode } from "vue";
import type { RemoteDrag } from "../../../boards/boardSession";
import type { Viewport } from "../../../boards/boardViewport";
import { motionMs } from "../../../motion/aiMotion";
import { createPointerGlide } from "../../../live/pointerGlide";
import { isAgentUser } from "../../../live/agentUser";
import CollaborationPointer from "../../CollaborationPointer.vue";

/**
 * Other people on the board (spec §3.5): their pointer with their name, and the boxes they drag, in screen space. Each person has
 * the server's colour, the one of their caret in a page (objectPresence.ts `userColor`); the name label is the editor's caret
 * label. Updates arrive at most 20 times a second; a pointer glides between them in board coordinates (pointerGlide.ts), so it
 * neither jumps nor lags behind the board when the viewport pans or zooms; a dragged box is carried over by a short transition.
 *
 * No TransitionGroup: it measures each of its children on every render, which lays out the whole board (≈ 120–200 ms per drag preview
 * and per start or stop on 300 cards, COL2-1). A mark fades in by its CSS animation, and out (at its last place) for
 * --atlas-motion-surface after its person stops; each mark follows its person on its own, the list changes only when one starts or stops.
 */
const props = defineProps<{ remote: readonly RemoteDrag[]; viewport: Viewport }>();
interface Mark { key: string; userId: string; itemId?: string }
const root = ref<HTMLElement>();
const screen = (x: number, y: number) => ({ x: x * props.viewport.zoom + props.viewport.x, y: y * props.viewport.zoom + props.viewport.y });
const live = computed<readonly Mark[]>(previous => {
  const next = props.remote.flatMap(user => [
    ...user.dragging.map(drag => ({ key: `${user.userId}:${drag.itemId}`, userId: user.userId, itemId: drag.itemId })),
    ...(user.cursor ? [{ key: `${user.userId}:cursor`, userId: user.userId }] : []),
  ]);
  return previous && previous.length === next.length && next.every((mark, index) => mark.key === previous[index]!.key) ? previous : next;
});
/** Marks whose person stopped, fading out. */
const leaving = shallowRef<ReadonlyMap<string, Mark>>(new Map());
const timers = new Set<ReturnType<typeof setTimeout>>();
watch(live, (next, previous = []) => {
  const keys = new Set(next.map(mark => mark.key));
  const gone = previous.filter(mark => !keys.has(mark.key));
  const kept = [...leaving.value].filter(([key]) => !keys.has(key));
  if (!gone.length && kept.length === leaving.value.size) return;
  leaving.value = new Map([...kept, ...gone.map(mark => [mark.key, mark] as const)]);
  if (!gone.length) return;
  const timer = setTimeout(() => {
    timers.delete(timer);
    leaving.value = new Map([...leaving.value].filter(([key]) => !gone.some(mark => mark.key === key)));
  }, motionMs(root.value ?? document.body, "--atlas-motion-surface"));
  timers.add(timer);
});
const glide = createPointerGlide(() => root.value ?? document.body, (element, x, y) => {
  const at = screen(x, y);
  element.style.transform = `translate(${at.x}px, ${at.y}px)`;
});
watch(() => [props.viewport.x, props.viewport.y, props.viewport.zoom], () => glide.redraw());
onBeforeUnmount(() => { timers.forEach(clearTimeout); glide.stop(); });
const shown = computed(() => [...live.value.map(mark => ({ mark, out: false })), ...[...leaving.value.values()].map(mark => ({ mark, out: true }))]);

interface Look { name?: string; agent?: boolean; style: Record<string, string>; at?: { x: number; y: number } }
/** What a mark draws now, read by the mark itself (props are reactive): a ghost box, or a pointer with the name; null once gone. */
function lookOf(mark: Mark): Look | null {
  const user = props.remote.find(person => person.userId === mark.userId);
  if (mark.itemId === undefined) {
    if (!user?.cursor) return null;
    return { name: user.displayName, agent: isAgentUser(user.userId), style: { "--collaboration-color": user.color }, at: { x: user.cursor.x, y: user.cursor.y } };
  }
  const drag = user?.dragging.find(entry => entry.itemId === mark.itemId);
  if (!user || !drag) return null;
  const at = screen(drag.x, drag.y), zoom = props.viewport.zoom;
  return { style: { "--collaboration-color": user.color, width: `${drag.width * zoom}px`, height: `${drag.height * zoom}px`, transform: `translate(${at.x}px, ${at.y}px)` } };
}
// A mark whose person stopped keeps its last look while it fades out.
const PresenceMark = defineComponent({
  props: { mark: { type: Object as PropType<Mark>, required: true }, out: Boolean },
  setup: mark => {
    let last: Look | null = null;
    // The pointer's element (CollaborationPointer's root), which the glide moves.
    const pointer = ref<{ $el: HTMLElement }>();
    watchEffect(() => {
      const at = lookOf(mark.mark)?.at;
      if (at && pointer.value) glide.to(pointer.value.$el, at.x, at.y);
    }, { flush: "post" });
    onBeforeUnmount(() => { if (pointer.value) glide.forget(pointer.value.$el); });
    return (): VNode | null => {
      const look = (last = lookOf(mark.mark) ?? last);
      if (!look) return null;
      const leavingClass = mark.out ? " is-leaving" : "";
      return mark.mark.itemId === undefined
        // An AI at work, as in the promo film: its arrow in the AI gradient, its stars writing in its name pill while it is on the board.
        ? h(CollaborationPointer, { ref: pointer, name: look.name ?? "", agent: look.agent, class: leavingClass.trim() || undefined, "data-board-cursor": "", style: look.style })
        : h("div", { class: `board-ghost${leavingClass}`, "data-board-ghost": "", style: look.style });
    };
  },
});
</script>

<template>
  <div ref="root" class="board-presence" aria-hidden="true">
    <PresenceMark v-for="entry in shown" :key="entry.mark.key" :mark="entry.mark" :out="entry.out" />
  </div>
</template>

<style scoped>
.board-presence { position: absolute; inset: 0; overflow: hidden; pointer-events: none; }
/* The pointer is the one over a page (collaborationPointer.css); a dragged box moves and fades the same way. */
.board-presence :deep(.board-ghost) { position: absolute; top: 0; left: 0; animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out);
  transition: transform var(--atlas-motion-fast) var(--atlas-ease-out), opacity var(--atlas-motion-surface) var(--atlas-ease-out); }
.board-presence :deep(.is-leaving) { opacity: 0; }
.board-presence :deep(.board-ghost) { border: 2px dashed var(--collaboration-color); border-radius: var(--atlas-radius-md); background: color-mix(in oklab, var(--collaboration-color) 10%, transparent); }
</style>
