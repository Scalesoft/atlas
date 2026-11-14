import { nextTick, onBeforeUnmount, ref, watch, type Ref, type WatchSource } from "vue";

/**
 * The one underline of a tab row that slides under the current tab (board 16: 200 ms, the move curve): the tabs of
 * Knihovna and of a space. `current` selects the current tab inside the row. The underline is placed without motion
 * first (`ready` turns true a frame later; the CSS transition of `.library-tabs-indicator.is-ready` hangs on it),
 * and follows the row when it resizes or when `source` changes. A row that leaves the page and comes back is placed
 * afresh. Bind it as `:style="{ width: `${width}px`, transform: `translateX(${left}px)` }"`.
 */
export function useTabIndicator(row: Ref<HTMLElement | null | undefined>, current: string, source: WatchSource) {
  const indicator = ref({ left: 0, width: 0, ready: false });
  function place() {
    const tab = row.value?.querySelector<HTMLElement>(current);
    if (!tab) return;
    indicator.value = { left: tab.offsetLeft, width: tab.offsetWidth, ready: indicator.value.ready };
    if (!indicator.value.ready) requestAnimationFrame(() => { indicator.value = { ...indicator.value, ready: true }; });
  }
  let observer: ResizeObserver | undefined;
  watch(row, (element) => {
    observer?.disconnect();
    observer = undefined;
    if (!element) { indicator.value = { left: 0, width: 0, ready: false }; return; }
    place();
    if (typeof ResizeObserver !== "function") return;
    observer = new ResizeObserver(() => place());
    observer.observe(element);
  }, { immediate: true, flush: "post" });
  watch(source, () => void nextTick(place));
  onBeforeUnmount(() => observer?.disconnect());
  return indicator;
}
