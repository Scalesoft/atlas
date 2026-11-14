import { animateAtlas } from "../../motion/aiMotion";

/**
 * One column of a table of rows laid out on a grid: `probe` is a cell of the first row (where the column stands),
 * `cells` every cell of the column, header included.
 */
export interface GlideColumn { probe: string; cells: string }
export interface GlideOptions { duration: number; easing: string }

/** Whether an element is at least partly inside the window (one that cannot be measured counts as inside). */
function inView(element: Element) {
  const { top, bottom } = element.getBoundingClientRect();
  return !(bottom < 0 || top > window.innerHeight);
}

/** Where each column stands now, as drawn (a glide that is still running is part of it); NaN for a column without a row. */
export function readColumns(table: HTMLElement, columns: readonly GlideColumn[]): number[] {
  return columns.map(({ probe }) => table.querySelector(probe)?.getBoundingClientRect().left ?? Number.NaN);
}

/**
 * The tracks of a grid cannot be animated for a hundred rows at once (every frame would lay all of them out again), so
 * the table changes its tracks in one step and each cell glides on the compositor from where it stood: `before` is
 * `readColumns` taken just before the change, the cells are moved back by what they travelled and eased to rest
 * (a FLIP). Only the cells that can be seen glide. A glide that was still running must be cancelled by the caller
 * first, so the position read now is the laid-out one. Under reduced motion `animateAtlas` leaves the travel out.
 * Returns the glides it started.
 */
export function glideColumns(table: HTMLElement, columns: readonly GlideColumn[], before: readonly number[], options: GlideOptions): Animation[] {
  const after = readColumns(table, columns);
  const started: Animation[] = [];
  columns.forEach(({ cells }, index) => {
    const distance = Math.round((before[index]! - after[index]!) * 10) / 10;
    if (!Number.isFinite(distance) || Math.abs(distance) < 0.5) return;
    for (const cell of table.querySelectorAll<HTMLElement>(cells)) {
      if (!inView(cell)) continue;
      const glide = animateAtlas(cell, [{ translate: `${distance}px 0` }, { translate: "0 0" }], { ...options, fill: "backwards" });
      if (glide) started.push(glide);
    }
  });
  return started;
}

/**
 * Cells that are about to be removed with their column (the checkboxes of selection): a copy of each one that can be
 * seen is pinned where it stands inside its own row, so it can leave after the column has gone. Taken just before the
 * removal. The copies are decoration: inert and hidden from assistive technology.
 */
export function ghostCells(table: HTMLElement, selector: string): HTMLElement[] {
  const ghosts: HTMLElement[] = [];
  for (const cell of table.querySelectorAll<HTMLElement>(selector)) {
    const row = cell.parentElement;
    if (!row || !inView(cell)) continue;
    const box = cell.getBoundingClientRect();
    const frame = row.getBoundingClientRect();
    const ghost = cell.cloneNode(true) as HTMLElement;
    cell.querySelectorAll<HTMLInputElement>("input").forEach((input, index) => { ghost.querySelectorAll<HTMLInputElement>("input")[index]!.checked = input.checked; });
    ghost.setAttribute("aria-hidden", "true");
    ghost.setAttribute("inert", "");
    ghost.removeAttribute("role");
    ghost.style.cssText = `position:absolute;left:${box.left - frame.left}px;top:${box.top - frame.top}px;width:${box.width}px;height:${box.height}px;margin:0;pointer-events:none`;
    row.append(ghost);
    ghosts.push(ghost);
  }
  return ghosts;
}

/** The cells of a column that has just appeared come in from `distance` along the row, fading in (only those that can be seen). */
export function enterCells(table: HTMLElement, selector: string, distance: number, options: GlideOptions): Animation[] {
  const started: Animation[] = [];
  for (const cell of table.querySelectorAll<HTMLElement>(selector)) {
    if (!inView(cell)) continue;
    const enter = animateAtlas(cell, [{ translate: `${distance}px 0`, opacity: 0 }, { translate: "0 0", opacity: 1 }], { ...options, fill: "backwards" });
    if (enter) started.push(enter);
  }
  return started;
}

/** The copies go the way their column went (`distance` along the row, fading) and are removed when they are gone. */
export function releaseGhosts(ghosts: readonly HTMLElement[], distance: number, options: GlideOptions) {
  for (const ghost of ghosts) {
    const leave = animateAtlas(ghost, [{ translate: "0 0", opacity: 1 }, { translate: `${distance}px 0`, opacity: 0 }], { ...options, fill: "forwards" });
    if (leave) leave.addEventListener("finish", () => ghost.remove());
    else ghost.remove();
  }
}
