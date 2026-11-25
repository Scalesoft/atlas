import { nextTick, onBeforeUnmount, onMounted, ref, watch, type Ref, type WatchSource } from "vue";

/**
 * The one indicator of a tab row that slides to the current tab (board 16: 200 ms, the move curve): the tabs of Knihovna and
 * of a space, the parts of a settings page (SettingsTabs: underline or raised segment) and the dock's tabs (WorkspaceSidePanel:
 * pill). `current` selects the current tab inside the row; `shown` is false while the row has none. It is placed without
 * motion first (`ready` turns true a frame later; the CSS transition hangs on it) and follows the row when the row resizes,
 * when the fonts arrive or when `source` changes. Nothing else measures it, so the page beside the row is never laid out for
 * it (perf 3.5). A row that leaves the page and comes back is placed afresh. Bind it as
 * `:style="{ width: `${width}px`, transform: `translateX(${left}px)` }"`.
 */
export function useTabIndicator(row: Ref<HTMLElement | null | undefined>, current: string, source: WatchSource) {
  const indicator = ref({ left: 0, top: 0, width: 0, height: 0, ready: false, shown: false });
  function place() {
    const tab = row.value?.querySelector<HTMLElement>(current);
    if (!tab) { if (indicator.value.shown) indicator.value = { ...indicator.value, shown: false }; return; }
    indicator.value = { left: tab.offsetLeft, top: tab.offsetTop, width: tab.offsetWidth, height: tab.offsetHeight, ready: indicator.value.ready, shown: true };
    if (!indicator.value.ready) requestAnimationFrame(() => { indicator.value = { ...indicator.value, ready: true }; });
  }
  let observer: ResizeObserver | undefined;
  watch(row, (element) => {
    observer?.disconnect();
    observer = undefined;
    if (!element) { indicator.value = { left: 0, top: 0, width: 0, height: 0, ready: false, shown: false }; return; }
    place();
    if (typeof ResizeObserver !== "function") return;
    observer = new ResizeObserver(() => place());
    observer.observe(element);
  }, { immediate: true, flush: "post" });
  watch(source, () => void nextTick(place));
  // A web font that arrives later widens the tabs without resizing a row that fills its line.
  onMounted(() => void document.fonts?.ready.then(place));
  onBeforeUnmount(() => observer?.disconnect());
  return indicator;
}
