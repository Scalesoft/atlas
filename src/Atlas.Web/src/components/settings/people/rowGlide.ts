import { animateAtlas, motionMs } from "../../../motion/aiMotion";

/**
 * A table body whose rows change (a filter takes some away and brings others) closes the gaps and makes room by gliding, not by
 * jumping. The browser does not do it for a table body (the leaving rows keep their room until they have faded and a move transition
 * has nothing to measure after that), so it is done by hand, once for every change of the rows:
 *   `tops(rows)` reads where the rows are to the eye just before the change (a glide that is still running counts, so an
 *   interrupted glide is continued from where it is), and `glide(rows, before)` follows it just after: every row that was seen
 *   and has moved travels from where it was seen to its new place. The first glide of a row is cancelled by the next one, which
 *   starts at the place the row has reached, so a second change within the first glide never moves a row against its way.
 * Rows that were not there before (they rise in by themselves) are left alone. Reduced motion keeps no travel (animateAtlas).
 */
export type RowTops = Map<Element, number>;
export interface RowGlideAnimation { cancel(): void }

/** Rows beyond this many are neither read nor glided: nobody sees the 200th row of a list move. */
const reach = 200;

function playGlide(row: Element, from: number): RowGlideAnimation | null {
  return animateAtlas(row, [{ transform: `translateY(${from}px)` }, { transform: "none" }], { duration: motionMs(row, "--atlas-motion-base") });
}

/**
 * The rows that leave a table fade first and are taken out when their fade ends: the rows that start to leave in the same tick (one
 * change of the filter) are one wave and end in the same frame. `wave(body)` counts a row into its wave when it starts to leave and
 * returns `finish(remove)`, which the row calls when its fade ends: the first of the wave reads where the rows are (a glide that
 * is still running included), every row removes itself, and the last one glides the rows that stay, in the same frame, so nothing
 * is painted in between. The next wave reads the places anew, so two changes close together never share one stale reading.
 * (The glide must not wait for an animation frame: a script that measures in a frame callback would see the jump.)
 */
export function createLeaveWave(glide: ReturnType<typeof createRowGlide>, endOfTick: (reset: () => void) => void = queueMicrotask) {
  let current: { pending: number; before?: RowTops } | undefined;
  return (body: Element | null) => {
    if (!current) {
      const started = (current = { pending: 0 });
      endOfTick(() => { if (current === started) current = undefined; });
    }
    const wave = current;
    wave.pending += 1;
    return (remove: () => void) => {
      if (body && !wave.before) wave.before = glide.tops(body.children);
      remove();
      wave.pending -= 1;
      if (wave.pending === 0 && body && wave.before) glide.glide(body.children, wave.before);
    };
  };
}

export function createRowGlide(play: (row: Element, from: number) => RowGlideAnimation | null = playGlide) {
  const running = new WeakMap<Element, RowGlideAnimation>();
  return {
    /** Where the rows are to the eye now, a glide that is still running included; call it just before the rows change. */
    tops(rows: Iterable<Element>, limit = reach): RowTops {
      const tops: RowTops = new Map();
      for (const row of rows) {
        if (tops.size >= limit) break;
        tops.set(row, row.getBoundingClientRect().top);
      }
      return tops;
    },
    /** Glides every row that was seen before and has moved by at least a pixel; returns how many it moved. Call it just after the change. */
    glide(rows: Iterable<Element>, before: RowTops): number {
      const seen = [...rows].filter((row) => before.has(row));
      // A running glide hides where the layout has put the row: take it away, then read, then start the next one where the eye saw the row.
      for (const row of seen) {
        running.get(row)?.cancel();
        running.delete(row);
      }
      let moved = 0;
      for (const row of seen) {
        const from = before.get(row)! - row.getBoundingClientRect().top;
        if (Math.abs(from) < 1) continue;
        const animation = play(row, from);
        if (animation) running.set(row, animation);
        moved += 1;
      }
      return moved;
    },
  };
}
