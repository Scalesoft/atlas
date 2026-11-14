import type { Geometry } from "./boardApi";

/**
 * Alignment guides with snapping while items move (interaction spec §3.2), in board coordinates; the caller passes the
 * threshold as 6 screen px in board units (6 / zoom) and leaves it out while Alt is held.
 *
 * Each axis snaps on its own to the nearest candidate within the threshold: an edge or the centre of the moved box on an edge or
 * the centre of a neighbour, or an even gap in its row (column): centred between the two neighbours, or continuing the gap the
 * row already has. `guides` are what to draw after the snap: an `align` guide is a line across at `at` (x = at for axis x)
 * from `from` to `to`; a `spacing` guide marks one gap along the axis, from `from` to `to`, at `at` on the other axis.
 */
export type Axis = "x" | "y";
export interface Guide { axis: Axis; at: number; from: number; to: number; kind: "align" | "spacing" }
export interface Snap { dx: number; dy: number; guides: Guide[] }

type Range = [number, number];
const span = (r: Geometry, axis: Axis): Range => (axis === "x" ? [r.x, r.x + r.width] : [r.y, r.y + r.height]);
const across = (r: Geometry, axis: Axis) => span(r, axis === "x" ? "y" : "x");
const lines = (r: Geometry, axis: Axis) => { const [start, end] = span(r, axis); return [start, (start + end) / 2, end]; };
const overlaps = (a: Range, b: Range) => a[0] < b[1] && b[0] < a[1];
const shift = (r: Geometry, axis: Axis, by: number): Geometry => (axis === "x" ? { ...r, x: r.x + by } : { ...r, y: r.y + by });
const EPSILON = 0.5;

interface Candidate { delta: number; gaps?: Range[]; involved?: Geometry[] }

function alignment(moving: Geometry, others: readonly Geometry[], axis: Axis, threshold: number): Candidate | undefined {
  let best: Candidate | undefined;
  for (const other of others) for (const target of lines(other, axis)) for (const line of lines(moving, axis)) {
    const delta = target - line;
    if (Math.abs(delta) <= threshold && (!best || Math.abs(delta) < Math.abs(best.delta))) best = { delta };
  }
  return best;
}

function spacing(moving: Geometry, others: readonly Geometry[], axis: Axis, threshold: number): Candidate | undefined {
  const row = others.filter(other => overlaps(across(other, axis), across(moving, axis)));
  const [start, end] = span(moving, axis);
  const size = end - start;
  const nearestBefore = (limit: number) => row.filter(o => span(o, axis)[1] <= limit).sort((a, b) => span(b, axis)[1] - span(a, axis)[1])[0];
  const nearestAfter = (limit: number) => row.filter(o => span(o, axis)[0] >= limit).sort((a, b) => span(a, axis)[0] - span(b, axis)[0])[0];
  const before = nearestBefore(start + threshold), after = nearestAfter(end - threshold);
  const options: (Candidate & { at: number })[] = [];
  if (before && after) {
    const at = (span(before, axis)[1] + span(after, axis)[0] - size) / 2;
    if (at >= span(before, axis)[1]) options.push({ at, delta: at - start, gaps: [[span(before, axis)[1], at], [at + size, span(after, axis)[0]]], involved: [before, after] });
  }
  const farBefore = before && nearestBefore(span(before, axis)[0]);
  if (before && farBefore) {
    const gap = span(before, axis)[0] - span(farBefore, axis)[1], at = span(before, axis)[1] + gap;
    options.push({ at, delta: at - start, gaps: [[span(farBefore, axis)[1], span(before, axis)[0]], [span(before, axis)[1], at]], involved: [farBefore, before] });
  }
  const farAfter = after && nearestAfter(span(after, axis)[1]);
  if (after && farAfter) {
    const gap = span(farAfter, axis)[0] - span(after, axis)[1], at = span(after, axis)[0] - gap - size;
    options.push({ at, delta: at - start, gaps: [[at + size, span(after, axis)[0]], [span(after, axis)[1], span(farAfter, axis)[0]]], involved: [after, farAfter] });
  }
  return options.filter(option => Math.abs(option.delta) <= threshold).sort((a, b) => Math.abs(a.delta) - Math.abs(b.delta))[0];
}

function alignGuides(moved: Geometry, others: readonly Geometry[], axis: Axis): Guide[] {
  const guides = new Map<number, Guide>();
  for (const line of lines(moved, axis)) for (const other of others) {
    if (!lines(other, axis).some(target => Math.abs(target - line) < EPSILON)) continue;
    const [a, b] = across(moved, axis), [c, d] = across(other, axis);
    const guide = guides.get(line);
    guides.set(line, { axis, at: line, from: Math.min(a, c, guide?.from ?? a), to: Math.max(b, d, guide?.to ?? b), kind: "align" });
  }
  return [...guides.values()];
}

/**
 * The board's grid (spec §3.7): a step in board units, and on a timeline the divisions of its visible level for x. Where no guide
 * to a neighbour applies on an axis, the moved box's top-left corner goes to the nearest grid line (guides win within 6 px).
 */
export interface GridSnap { step: number; xTicks?: readonly number[] }
const nearest = (value: number, lines: readonly number[]) => lines.reduce((best, line) => (Math.abs(line - value) < Math.abs(best - value) ? line : best), lines[0] ?? value);
const onGrid = (value: number, step: number) => Math.round(value / step) * step;

/** A resized box with its corner and size on the grid, at least one step large. */
export const snapResize = (box: Geometry, step: number): Geometry => ({
  x: onGrid(box.x, step), y: onGrid(box.y, step), width: Math.max(step, onGrid(box.width, step)), height: Math.max(step, onGrid(box.height, step)),
});

export function snapMove(moving: Geometry, others: readonly Geometry[], threshold: number, grid?: GridSnap): Snap {
  const result: Snap = { dx: 0, dy: 0, guides: [] };
  let moved = moving;
  const spacingGuides: Guide[] = [];
  for (const axis of ["x", "y"] as const) {
    const aligned = alignment(moving, others, axis, threshold), even = spacing(moving, others, axis, threshold);
    const chosen = even && (!aligned || Math.abs(even.delta) < Math.abs(aligned.delta)) ? even : aligned;
    if (!chosen) {
      if (grid) {
        const start = axis === "x" ? moving.x : moving.y;
        const target = axis === "x" && grid.xTicks?.length ? nearest(start, grid.xTicks) : onGrid(start, grid.step);
        result[axis === "x" ? "dx" : "dy"] = target - start;
        moved = shift(moved, axis, target - start);
      }
      continue;
    }
    result[axis === "x" ? "dx" : "dy"] = chosen.delta;
    moved = shift(moved, axis, chosen.delta);
    if (chosen.gaps) {
      const boxes = [...chosen.involved!, shift(moving, axis, chosen.delta)].map(box => across(box, axis));
      const at = (Math.max(...boxes.map(([lo]) => lo)) + Math.min(...boxes.map(([, hi]) => hi))) / 2;
      spacingGuides.push(...chosen.gaps.map(([from, to]): Guide => ({ axis, at, from, to, kind: "spacing" })));
    }
  }
  if (result.dx === 0 && result.dy === 0 && !alignment(moving, others, "x", 0) && !alignment(moving, others, "y", 0) && !spacingGuides.length) return result;
  result.guides = [...alignGuides(moved, others, "x"), ...alignGuides(moved, others, "y"), ...spacingGuides];
  return result;
}
