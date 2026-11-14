import type { Geometry } from "./boardApi";
import { centreOf } from "./geometry";

type Placed = Geometry & { id: string };

/**
 * The order Tab walks the items in (J1): rows top to bottom, each left to right. An item whose top lies within half the smallest
 * height of the row started above it belongs to that row, so a grid of cards a few pixels out of line still reads by rows.
 */
export function readingOrder(items: readonly Placed[]): string[] {
  const rows: Placed[][] = [];
  for (const item of [...items].sort((a, b) => a.y - b.y || a.x - b.x)) {
    const row = rows.at(-1);
    if (row && item.y < row[0]!.y + Math.min(...row.map(other => other.height)) / 2) row.push(item); else rows.push([item]);
  }
  return rows.flatMap(row => row.sort((a, b) => a.x - b.x).map(item => item.id));
}

export type Direction = "left" | "right" | "up" | "down";
/** The item an arrow points at from `from` (keyboard connecting, J1): beyond its centre that way, nearest, an offset across counting double. */
export function nearestInDirection(from: Placed, items: readonly Placed[], direction: Direction): string | undefined {
  const start = centreOf(from), horizontal = direction === "left" || direction === "right", sign = direction === "right" || direction === "down" ? 1 : -1;
  let best: { id: string; score: number } | undefined;
  for (const item of items) {
    if (item.id === from.id) continue;
    const centre = centreOf(item);
    const along = ((horizontal ? centre.x - start.x : centre.y - start.y) * sign), aside = Math.abs(horizontal ? centre.y - start.y : centre.x - start.x);
    if (along <= 0) continue;
    const score = along + 2 * aside;
    if (!best || score < best.score) best = { id: item.id, score };
  }
  return best?.id;
}

/**
 * Where a new item goes: `wanted`, or, when another item sits on that spot, the next spot down and to the right, a `step` at
 * a time, that is free and keeps the item inside the `visible` part of the board (all in board coordinates). An item less than
 * half a step off counts as being on the spot, so a new note never hides the one under it.
 * shortcut: one diagonal only; when it is full the item goes on the wanted spot, so add a second lane when boards get that crowded.
 */
export function freeSpot(wanted: Geometry, others: readonly Geometry[], visible: Geometry, step = 24): Geometry {
  const taken = (x: number, y: number) => others.some(other => Math.abs(other.x - x) < step / 2 && Math.abs(other.y - y) < step / 2);
  for (let k = 0; ; k++) {
    const x = wanted.x + k * step, y = wanted.y + k * step;
    if (k > 0 && (x + wanted.width > visible.x + visible.width || y + wanted.height > visible.y + visible.height)) return wanted;
    if (!taken(x, y)) return { ...wanted, x, y };
  }
}
