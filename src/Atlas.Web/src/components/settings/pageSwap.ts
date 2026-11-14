import { nextTick, watch, type WatchSource } from "vue";
import { outOfReach } from "../../lib/outOfReach";

/**
 * The hooks of the `<Transition name="st-page">` that swaps the pages of a settings surface (the sections of Správa wiki, of a
 * space, of the profile), one helper for all three. The leaving page is taken out of the flow (settings.css) and fades where it
 * stands, so while the reader is scrolled down:
 * - the new page starts at the top of the document (`beforeEnter`), not above the fold under the old one,
 * - the leaving page stays exactly where the reader saw it for the length of its fade (`beforeLeave`): the scroll is reset in the
 *   same step, so it is moved up by the scroll distance, and it is out of reach of the keyboard and of assistive technology.
 * A swap is the update that follows a change of `key` (the address of the surface or its current page): the skeleton a lazily
 * loaded page shows first is replaced by that page with the same hooks, and neither a scroll the reader has made then nor the
 * skeleton is touched. The first page of a visit has no swap either.
 */
export function usePageSwap(key: WatchSource) {
  let swapping = false;
  watch(key, () => {
    swapping = true;
    // The flag lives for the update this change causes: two ticks, the first lets the render be queued, the second waits for it.
    void Promise.resolve().then(() => nextTick(() => { swapping = false; }));
  }, { flush: "sync" });
  const scrolled = () => document.scrollingElement?.scrollTop ?? 0;
  return {
    beforeLeave(element: Element) {
      outOfReach(element);
      const distance = swapping ? scrolled() : 0;
      if (distance) (element as HTMLElement).style.translate = `0 ${-distance}px`;
    },
    beforeEnter() {
      if (!swapping) return;
      swapping = false;
      window.scrollTo({ top: 0, behavior: "instant" });
    },
  };
}
