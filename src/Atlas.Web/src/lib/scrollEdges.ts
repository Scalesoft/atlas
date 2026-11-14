import type { ObjectDirective } from "vue";
import { scrollBehavior } from "../motion/aiMotion";

/**
 * `v-scroll-edges` on a list that scrolls inside a fixed region (the lists of the rail): keeps `data-more-above`
 * and `data-more-below` on it while content waits beyond that edge, so CSS can fade the edge instead of cutting the
 * list like its end (refresh.css §2). It follows scrolling, the region's size and the size of what is inside it.
 * `v-scroll-edges:x` does the same for a row that scrolls sideways (the tabs of a space on a phone): `data-more-before`
 * and `data-more-after`.
 *
 * The edges are read once per frame. Read at once, on mount and after every change of the children, the scroll
 * geometry forced a layout of the whole page in the middle of a view change, several times over: entering the
 * settings a second time took 579 ms instead of 209 ms on a slow machine (CPU ×4).
 */
const watchers = new WeakMap<HTMLElement, () => void>();

export const vScrollEdges: ObjectDirective<HTMLElement> = {
  mounted(list, binding) {
    const mark = binding.arg === "x"
      ? () => {
        list.toggleAttribute("data-more-before", list.scrollLeft > 1);
        list.toggleAttribute("data-more-after", list.scrollWidth - list.clientWidth - list.scrollLeft > 1);
      }
      : () => {
        list.toggleAttribute("data-more-above", list.scrollTop > 1);
        list.toggleAttribute("data-more-below", list.scrollHeight - list.clientHeight - list.scrollTop > 1);
      };
    let frame = 0;
    const schedule = () => { frame ||= requestAnimationFrame(() => { frame = 0; mark(); }); };
    // A size change is reported after layout, so reading there costs nothing.
    const sizes = typeof ResizeObserver === "function" ? new ResizeObserver(mark) : undefined;
    const observe = () => {
      sizes?.disconnect();
      sizes?.observe(list);
      for (const child of list.children) sizes?.observe(child);
      schedule();
    };
    // The content is rendered by others (the tree, the teleported settings sections): watch what comes and goes.
    const children = typeof MutationObserver === "function" ? new MutationObserver(observe) : undefined;
    children?.observe(list, { childList: true });
    list.addEventListener("scroll", schedule, { passive: true });
    observe();
    watchers.set(list, () => {
      sizes?.disconnect();
      children?.disconnect();
      list.removeEventListener("scroll", schedule);
      if (frame) cancelAnimationFrame(frame);
    });
  },
  unmounted(list) {
    watchers.get(list)?.();
    watchers.delete(list);
  },
};

/** Scrolls a row that scrolls sideways just enough to show `item` with a margin (inline: nearest), never the page around it. */
export function revealInRow(row: HTMLElement, item: HTMLElement, margin = 16) {
  if (row.scrollWidth <= row.clientWidth) return;
  const start = item.offsetLeft - margin, end = item.offsetLeft + item.offsetWidth + margin;
  const left = start < row.scrollLeft ? start : end > row.scrollLeft + row.clientWidth ? end - row.clientWidth : row.scrollLeft;
  if (left !== row.scrollLeft) row.scrollTo({ left: Math.max(0, left), behavior: scrollBehavior(row) });
}
