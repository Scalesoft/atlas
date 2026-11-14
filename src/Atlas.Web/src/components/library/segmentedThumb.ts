import { computed, ref, watch, type Ref, type WatchSource } from "vue";
import { prefersReducedMotion } from "../../motion/aiMotion";
import { useTabIndicator } from "./tabIndicator";
import "./segmentedThumb.css";

/** What `v-bind` puts on the track: nothing under reduced motion, only `data-segmented` while there is no box to draw. */
export interface SegmentedThumbAttributes { "data-segmented"?: string; "data-thumb"?: "placed" | "ready"; style?: Record<string, string> }

/**
 * The raised segment of a segmented switch as one thumb that glides to the chosen option, the way the underline of the
 * tabs does (tabIndicator.ts), the segmented SettingsTabs and the Karty / Seznam switch of Knihovna do. `track` is the
 * element that holds the options, `current` selects the chosen one inside it and `source` changes when the choice does.
 * Bind the result on the track (`v-bind="thumb"`): `data-segmented` from the first render on (it makes the track the
 * containing block of its options, so the very first measurement is relative to the track and not to some ancestor),
 * `data-thumb="placed"` and the place of the thumb (`--thumb-x`, `--thumb-w`) once the chosen option has a box, and
 * `data-thumb="ready"` once gliding is allowed. The track brings its own look through `--thumb-inset` (its padding),
 * `--thumb-radius` (concentric: its radius − border − padding) and, where they differ, `--thumb-bg` and `--thumb-shadow`
 * (segmentedThumb.css), and drops the chip of the chosen option while `data-thumb` is on, so the switch looks the same at
 * rest and only a change of choice moves.
 *
 * The thumb is first placed without motion, and so is a switch that arrives hidden, folded or fading (a ghost of an
 * edit transition): the glide waits for the first real box, so it never travels in from the corner. Under reduced
 * motion nothing is bound: no thumb is drawn and the chosen option fades its own chip.
 */
export function useSegmentedThumb(track: Ref<HTMLElement | null | undefined>, current: string, source: WatchSource) {
  const indicator = useTabIndicator(track, current, source);
  // The helper's `ready` turns true a frame after the first placement even when that placement measured nothing.
  const measured = ref(false);
  watch(() => indicator.value.width, (width) => {
    if (width > 0 && !measured.value) requestAnimationFrame(() => { measured.value = true; });
  }, { immediate: true });
  return computed<SegmentedThumbAttributes>(() => {
    if (prefersReducedMotion(track.value)) return {};
    const { left, width, ready } = indicator.value;
    if (!width) return { "data-segmented": "" };
    return {
      "data-segmented": "",
      "data-thumb": ready && measured.value ? "ready" : "placed",
      style: { "--thumb-x": `${left}px`, "--thumb-w": `${width}px` },
    };
  });
}

/**
 * The same thumb for a track that another component draws (the tabs of the open workspace come from the editor
 * package, whose own DOM is never touched except for these attributes): the element is found by the host and the
 * attributes of `useSegmentedThumb` are put on it. The package renders no `style`, `data-segmented` or `data-thumb` of
 * its own, so its re-renders leave them alone.
 */
export function bindSegmentedThumb(track: Ref<HTMLElement | null | undefined>, current: string, source: WatchSource) {
  // Before the first measurement (registered first, so it runs first): the track is the containing block of its options.
  watch(track, (element) => { if (element && !prefersReducedMotion(element)) element.setAttribute("data-segmented", ""); }, { immediate: true, flush: "pre" });
  const thumb = useSegmentedThumb(track, current, source);
  watch([track, thumb], ([element, attributes]) => {
    if (!element) return;
    element.toggleAttribute("data-segmented", "data-segmented" in attributes);
    if (attributes["data-thumb"]) element.setAttribute("data-thumb", attributes["data-thumb"]); else element.removeAttribute("data-thumb");
    for (const name of ["--thumb-x", "--thumb-w"]) {
      const value = attributes.style?.[name];
      if (value === undefined) element.style.removeProperty(name); else element.style.setProperty(name, value);
    }
  }, { flush: "post", immediate: true });
}
