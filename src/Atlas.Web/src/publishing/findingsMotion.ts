import { animateAtlas, drawIcon, flashRow, literalMs } from "../motion/aiMotion";

/**
 * The rows of the publication check (prototype "Založení a publikace", moment 6; plan §9): each row starts as a
 * spinner, the panel grows by the row (160 ms), the result icon draws itself (240 ms) and the row flashes once; then
 * the change summary and the buttons unfold (240 ms). The prototype paces rows 540 ms apart for the storyboard; here
 * the whole sequence must add no more than about 600 ms, so rows follow each other at most 120 ms apart. Content is
 * in the DOM from the start: the motion only shows that the check ran and never delays the result.
 */
export const CHECK_ROW_GROW_MS = 160;
export const CHECK_ICON_DRAW_MS = 240;
export const CHECK_FOOTER_GROW_MS = 240;
export const CHECK_ROW_STEP_MS = 120;
export const CHECK_MOTION_BUDGET_MS = 600;

export interface CheckRowSchedule {
  /** Time between two rows. */
  readonly step: number;
  /** When row `index` has grown in and gets its icon. */
  settleAt(index: number): number;
  /** When the change summary and the buttons start to unfold. */
  readonly footerAt: number;
}

export function checkRowSchedule(count: number): CheckRowSchedule {
  const spread = CHECK_MOTION_BUDGET_MS - CHECK_ROW_GROW_MS - CHECK_ICON_DRAW_MS;
  const step = count > 1 ? Math.min(CHECK_ROW_STEP_MS, spread / (count - 1)) : 0;
  return {
    step,
    settleAt: index => index * step + CHECK_ROW_GROW_MS,
    footerAt: Math.max(0, count - 1) * step + CHECK_ROW_GROW_MS,
  };
}

/** Unfolds an element that is already in place, after `delay` (the prototype's grow of the panel by one row). */
export function unfoldIn(el: HTMLElement, delay: number, ms: number): Animation | null {
  const height = el.offsetHeight;
  return animateAtlas(el, [
    { height: "0px", opacity: 0, overflow: "hidden" },
    { height: `${height}px`, opacity: 1, overflow: "hidden" },
  ], { duration: literalMs(el, ms), delay: literalMs(el, delay) });
}

/** A row got its result: the icon draws itself and the row flashes once. */
export function settleCheckRow(row: HTMLElement): Animation[] {
  const icon = row.querySelector<SVGElement>(".publish-finding-icon svg");
  const flash = flashRow(row);
  return [...(icon ? drawIcon(icon) : []), ...(flash ? [flash] : [])];
}
