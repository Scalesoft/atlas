import { glow, RISE_IN_ROWS, riseIn } from "./motion/aiMotion";

/**
 * Rows that arrive with the full search pass (prototype "Hledání s odpovědí", moments 2 and 3; plan O15): they always rise
 * (`RISE_IN_ROWS`: 8 px, 200 ms, 40 ms apart; with or without AI) and a meaning-only row glows once on top. The prefix
 * preview never animates. Only the first arrival of a row animates, never a reordering; the DOM is final at once (the
 * primitives animate on top of it) and reduced motion is handled by them. A row has no CSS animation of its own.
 */

export function createArrivalTracker() {
  const seen = new Set<string>();
  return {
    reset() { seen.clear(); },
    /** `rows` are the row elements by result key, or a lookup run only for a new key (thousands of rows, one DOM read per arrival); `animate` is false for the prefix preview (the full pass animates). */
    settle(rows: Iterable<[string, Element | null | (() => Element | null)]>, animate: boolean) {
      const fresh: Element[] = [];
      for (const [key, found] of rows) {
        if (seen.has(key)) continue;
        seen.add(key);
        const el = typeof found === "function" ? found() : found;
        if (el) fresh.push(el);
      }
      if (!animate || !fresh.length) return;
      // A batch of 50 (a page of results, the next one while scrolling) would keep its last row invisible for 2 s: past the
      // first eight rows they rise together with the eighth (RISE_IN_ROWS.cap).
      riseIn(fresh, RISE_IN_ROWS);
      for (const el of fresh) if (el.hasAttribute("data-semantic")) glow(el);
    },
  };
}
