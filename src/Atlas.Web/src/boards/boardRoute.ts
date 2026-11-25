import { getBezierPath, getStraightPath, type Position } from "@vue-flow/core";
import type { BoardRoute, BoardSide, Geometry } from "./boardApi";

/**
 * Where a connector runs (the elbow route): out of its start side and into its end side in right angles, around the cards in the
 * way (a shortest path with few bends over the grid of the cards' outlines, inflated by a margin), corners rounded. Parallel
 * connectors between the same two items are spread apart, and the label sits halfway along the line. Pure functions, no Vue.
 */
export interface Point { x: number; y: number }
export interface Port extends Point { side: BoardSide }

const directions: Record<BoardSide, Point> = { top: { x: 0, y: -1 }, right: { x: 1, y: 0 }, bottom: { x: 0, y: 1 }, left: { x: -1, y: 0 } };
const steps = [{ x: 0, y: -1 }, { x: 1, y: 0 }, { x: 0, y: 1 }, { x: -1, y: 0 }];
const stepOf = (side: BoardSide) => steps.findIndex(step => step.x === directions[side].x && step.y === directions[side].y);
const out = (port: Port, length: number): Point => ({ x: port.x + directions[port.side].x * length, y: port.y + directions[port.side].y * length });
const inflate = (box: Geometry, by: number): Geometry => ({ x: box.x - by, y: box.y - by, width: box.width + 2 * by, height: box.height + 2 * by });
/** Strictly inside: a line along a box's outline does not cross it. */
const inside = (p: Point, b: Geometry) => p.x > b.x && p.x < b.x + b.width && p.y > b.y && p.y < b.y + b.height;
const at = ({ x, y }: Point): Point => ({ x, y });
const unique = (values: number[]) => [...new Set(values)].sort((a, b) => a - b);

/** The points without the ones that lie on a straight run between their neighbours. */
function simplify(points: readonly Point[]): Point[] {
  const result: Point[] = [];
  for (const point of points) {
    const last = result.at(-1), before = result.at(-2);
    if (last && last.x === point.x && last.y === point.y) continue;
    if (last && before && ((before.x === last.x && last.x === point.x) || (before.y === last.y && last.y === point.y))) result[result.length - 1] = point;
    else result.push(point);
  }
  return result;
}

/** A plain elbow: through the middle between the two stubs, turning on the axis the start leaves along. */
function elbow(from: Port, to: Port, s: Point, t: Point): Point[] {
  const across = from.side === "left" || from.side === "right";
  const middle = across ? [{ x: (s.x + t.x) / 2, y: s.y }, { x: (s.x + t.x) / 2, y: t.y }] : [{ x: s.x, y: (s.y + t.y) / 2 }, { x: t.x, y: (s.y + t.y) / 2 }];
  return simplify([at(from), s, ...middle, t, at(to)]);
}

/** How many of the rectangles (inclusive column and row ranges `[c0, c1, r0, r1]`; an empty one covers nothing) cover each point of
 * a columns × rows grid: a difference grid, summed once. */
function coverage(columns: number, rows: number, rectangles: readonly (readonly [number, number, number, number])[]): Int32Array {
  const w = columns + 1, d = new Int32Array(w * (rows + 1)), counts = new Int32Array(columns * rows);
  const add = (i: number, by: number) => { d[i] = d[i]! + by; };
  for (const [c0, c1, r0, r1] of rectangles) {
    if (c0 > c1 || r0 > r1) continue;
    add(r0 * w + c0, 1); add(r0 * w + c1 + 1, -1); add((r1 + 1) * w + c0, -1); add((r1 + 1) * w + c1 + 1, 1);
  }
  for (let r = 0; r < rows; r++) for (let c = 0; c < columns; c++) {
    const i = r * w + c;
    add(i, (c ? d[i - 1]! : 0) + (r ? d[i - w]! : 0) - (c && r ? d[i - w - 1]! : 0));
    counts[r * columns + c] = d[i]!;
  }
  return counts;
}

/** A small binary heap of [cost, state]. */
class Queue {
  private heap: [number, number][] = [];
  get size() { return this.heap.length; }
  push(entry: [number, number]) {
    const heap = this.heap;
    heap.push(entry);
    for (let at = heap.length - 1; at > 0;) {
      const up = (at - 1) >> 1;
      if (heap[up]![0] <= heap[at]![0]) break;
      [heap[up], heap[at]] = [heap[at]!, heap[up]!];
      at = up;
    }
  }
  pop(): [number, number] {
    const heap = this.heap, top = heap[0]!, last = heap.pop()!;
    if (heap.length) {
      heap[0] = last;
      for (let at = 0; ;) {
        const left = 2 * at + 1, right = left + 1;
        let least = at;
        if (left < heap.length && heap[left]![0] < heap[least]![0]) least = left;
        if (right < heap.length && heap[right]![0] < heap[least]![0]) least = right;
        if (least === at) break;
        [heap[least], heap[at]] = [heap[at]!, heap[least]!];
        at = least;
      }
    }
    return top;
  }
}

export interface RouteOptions { margin?: number; stub?: number; bend?: number }
/**
 * The elbow route from one port to the other around `obstacles` (the cards, the connector's own two included): a stub straight
 * out of each side, then the shortest way with the fewest bends along the cards' inflated outlines and the middle between the
 * stubs. When the cards leave no way, the plain elbow.
 */
export function routeElbow(from: Port, to: Port, obstacles: readonly Geometry[], options: RouteOptions = {}): Point[] {
  const { margin = 16, stub = 24, bend = 48 } = options;
  const s = out(from, stub), t = out(to, stub);
  const boxes = obstacles.map(box => inflate(box, margin));
  if (boxes.some(box => inside(s, box) || inside(t, box))) return elbow(from, to, s, t);
  const xs = unique([s.x, t.x, (s.x + t.x) / 2, ...boxes.flatMap(box => [box.x, box.x + box.width])]);
  const ys = unique([s.y, t.y, (s.y + t.y) / 2, ...boxes.flatMap(box => [box.y, box.y + box.height])]);
  const columns = xs.length, rows = ys.length, cells = columns * rows;
  const pointOf = (cell: number): Point => ({ x: xs[cell % columns]!, y: ys[Math.floor(cell / columns)]! });
  const column = new Map(xs.map((x, index) => [x, index])), row = new Map(ys.map((y, index) => [y, index]));
  // Every box's outline lies on grid lines, so between two neighbouring lines a box is all across or not there: a point is blocked
  // when a box holds it strictly inside, a step to the next point when a box holds its middle. Marked once per box, not tested against
  // every box at every step of the search (that was most of a drag frame, connectors × steps × boxes).
  const spans = boxes.map(box => [column.get(box.x)!, column.get(box.x + box.width)!, row.get(box.y)!, row.get(box.y + box.height)!] as const);
  const blockedAt = coverage(columns, rows, spans.map(([left, right, top, bottom]) => [left + 1, right - 1, top + 1, bottom - 1] as const));
  const blockedAcross = coverage(columns, rows, spans.map(([left, right, top, bottom]) => [left, right - 1, top + 1, bottom - 1] as const));
  const blockedDown = coverage(columns, rows, spans.map(([left, right, top, bottom]) => [left + 1, right - 1, top, bottom - 1] as const));
  const start = column.get(s.x)! + row.get(s.y)! * columns, goal = column.get(t.x)! + row.get(t.y)! * columns;
  // Moving into the end port runs against its side's direction.
  const entering = (stepOf(to.side) + 2) % 4;
  const near = (cell: number) => cell === start || cell === goal;
  const estimate = (cell: number) => { const p = pointOf(cell); return Math.abs(p.x - t.x) + Math.abs(p.y - t.y); };
  // A state is a cell and the direction it was reached in.
  const best = new Float64Array(cells * 4).fill(Infinity), previous = new Int32Array(cells * 4).fill(-1);
  const queue = new Queue();
  const first = start * 4 + stepOf(from.side);
  best[first] = 0;
  queue.push([estimate(start), first]);
  let found = -1;
  while (queue.size) {
    const [, state] = queue.pop();
    const cell = state >> 2, heading = state & 3, cost = best[state]!;
    if (cell === goal) { found = state; break; }
    const atColumn = cell % columns, atRow = Math.floor(cell / columns);
    for (let next = 0; next < 4; next++) {
      if (next === (heading + 2) % 4) continue;
      const step = steps[next]!, c = atColumn + step.x, r = atRow + step.y;
      if (c < 0 || c >= columns || r < 0 || r >= rows) continue;
      const target = c + r * columns;
      if (blockedAt[target] || (step.x ? blockedAcross : blockedDown)[Math.min(cell, target)]) continue;
      const a = pointOf(cell), b = pointOf(target);
      // A bend right at a stub hugs the card: it costs more than one in the open.
      let added = Math.abs(a.x - b.x) + Math.abs(a.y - b.y) + (next !== heading ? bend * (near(cell) ? 1.5 : 1) : 0);
      if (target === goal && next !== entering) added += bend;
      const key = target * 4 + next;
      if (cost + added < best[key]!) {
        best[key] = cost + added;
        previous[key] = state;
        queue.push([cost + added + estimate(target), key]);
      }
    }
  }
  if (found < 0) return elbow(from, to, s, t);
  const path: Point[] = [];
  for (let state = found; state >= 0; state = previous[state]!) path.unshift(pointOf(state >> 2));
  return simplify([at(from), ...path, at(to)]);
}

/** An SVG path through the points, each corner rounded by `radius` at most half of the shorter segment beside it. */
export function roundedPath(points: readonly Point[], radius = 12): string {
  const n = (value: number) => Math.round(value * 100) / 100;
  if (!points.length) return "";
  let d = `M${n(points[0]!.x)} ${n(points[0]!.y)}`;
  for (let index = 1; index < points.length - 1; index++) {
    const before = points[index - 1]!, at = points[index]!, after = points[index + 1]!;
    const into = Math.hypot(at.x - before.x, at.y - before.y), away = Math.hypot(after.x - at.x, after.y - at.y);
    if (!into || !away) continue; // a doubled point (cards not measured yet) has no corner to round
    const r = Math.min(radius, into / 2, away / 2);
    const a = { x: at.x - ((at.x - before.x) / into) * r, y: at.y - ((at.y - before.y) / into) * r };
    const b = { x: at.x + ((after.x - at.x) / away) * r, y: at.y + ((after.y - at.y) / away) * r };
    d += `L${n(a.x)} ${n(a.y)}Q${n(at.x)} ${n(at.y)} ${n(b.x)} ${n(b.y)}`;
  }
  const last = points.at(-1)!;
  return points.length > 1 ? `${d}L${n(last.x)} ${n(last.y)}` : d;
}

/** The point halfway along the line: where its label goes. */
export function pointAtHalf(points: readonly Point[]): Point {
  const lengths = points.slice(1).map((point, index) => Math.hypot(point.x - points[index]!.x, point.y - points[index]!.y));
  let left = lengths.reduce((sum, length) => sum + length, 0) / 2;
  for (let index = 0; index < lengths.length; index++) {
    const length = lengths[index]!, a = points[index]!, b = points[index + 1]!;
    if (left <= length) return length ? { x: a.x + ((b.x - a.x) * left) / length, y: a.y + ((b.y - a.y) * left) / length } : a;
    left -= length;
  }
  return points.at(-1) ?? { x: 0, y: 0 };
}

/** How far each of several connectors between the same two items moves along its sides, spread around the middle; none for a single one. */
export function parallelOffsets(edges: readonly { id: string; source: string; target: string }[], gap = 14): Map<string, number> {
  const pairs = new Map<string, string[]>();
  for (const edge of edges) {
    const key = edge.source < edge.target ? `${edge.source}|${edge.target}` : `${edge.target}|${edge.source}`;
    pairs.set(key, [...(pairs.get(key) ?? []), edge.id]);
  }
  const offsets = new Map<string, number>();
  for (const ids of pairs.values()) if (ids.length > 1) ids.forEach((id, index) => offsets.set(id, (index - (ids.length - 1) / 2) * gap));
  return offsets;
}

/**
 * A connector's path and its label's point, as an edge and the preview of one being drawn route it (BoardEdge, BoardConnectorPreview):
 * curved and straight as Vue Flow draws them, the elbow around `boxes` (its own ends and the cards near them) with rounded corners.
 */
export function connectorPath(route: BoardRoute, source: Port, target: Port, boxes: readonly Geometry[]): [string, number, number] {
  const ends = { sourceX: source.x, sourceY: source.y, targetX: target.x, targetY: target.y, sourcePosition: source.side as Position, targetPosition: target.side as Position };
  if (route === "curved") { const [path, x, y] = getBezierPath(ends); return [path, x, y]; }
  if (route === "straight") { const [path, x, y] = getStraightPath(ends); return [path, x, y]; }
  const points = routeElbow(source, target, boxes);
  const middle = pointAtHalf(points);
  return [roundedPath(points, 12), middle.x, middle.y];
}
/** The cards an elbow between two ports goes around: in a box around both, a card's width wider, without its own ends, at most 60. */
export function cardsNear<T extends Geometry & { id: string }>(cards: readonly T[], source: Point, target: Point, ends: readonly (string | undefined)[]): T[] {
  const near = { x: Math.min(source.x, target.x) - 320, y: Math.min(source.y, target.y) - 320, right: Math.max(source.x, target.x) + 320, bottom: Math.max(source.y, target.y) + 320 };
  return boxesIn(cards, near).filter(box => !ends.includes(box.id)).slice(0, 60);
}

/** A port moved along its side (across the direction it leaves in). */
export const shiftPort = (port: Port, by: number): Port =>
  port.side === "top" || port.side === "bottom" ? { ...port, x: port.x + by } : { ...port, y: port.y + by };

/**
 * The boxes that overlap `area`, in their order: what filtering all of them gives, through a coarse grid built once per array (a
 * change of the board makes a new one). Every connector asks for the cards near it, and filtering all of them for each connector
 * cost connectors × cards on every change of the items.
 */
export function boxesIn<T extends Geometry>(boxes: readonly T[], area: Point & { right: number; bottom: number }): T[] {
  const grid = gridOf(boxes), found = new Set<number>();
  for (let column = cellOf(area.x); column <= cellOf(area.right); column++)
    for (let row = cellOf(area.y); row <= cellOf(area.bottom); row++)
      for (const index of grid.get(`${column} ${row}`) ?? []) found.add(index);
  return [...found].sort((a, b) => a - b).map(index => boxes[index]!)
    .filter(box => box.x < area.right && box.x + box.width > area.x && box.y < area.bottom && box.y + box.height > area.y);
}
const CELL = 512;
const cellOf = (value: number) => Math.floor(value / CELL);
const grids = new WeakMap<readonly Geometry[], Map<string, number[]>>();
function gridOf(boxes: readonly Geometry[]): Map<string, number[]> {
  const known = grids.get(boxes);
  if (known) return known;
  const grid = new Map<string, number[]>();
  boxes.forEach((box, index) => {
    for (let column = cellOf(box.x); column <= cellOf(box.x + box.width); column++)
      for (let row = cellOf(box.y); row <= cellOf(box.y + box.height); row++) {
        const key = `${column} ${row}`, cell = grid.get(key);
        if (cell) cell.push(index); else grid.set(key, [index]);
      }
  });
  grids.set(boxes, grid);
  return grid;
}
