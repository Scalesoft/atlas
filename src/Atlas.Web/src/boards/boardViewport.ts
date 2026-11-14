import type { Geometry } from "./boardApi";
import type { BoardItem } from "./boardModel";

/**
 * Navigation of the board plane (interaction spec §2), pure: screen = board · zoom + (x, y), in px of the canvas element. The
 * canvas handles the wheel itself (Vue Flow's wheel handling zooms Ctrl+wheel only on macOS), so a mouse, a trackpad and a pinch
 * behave the same everywhere.
 */
export interface Viewport { x: number; y: number; zoom: number }
export interface WheelInput { deltaX: number; deltaY: number; deltaMode: number; ctrlKey: boolean; metaKey: boolean; shiftKey: boolean }

export const MIN_ZOOM = 0.05, MAX_ZOOM = 4;
const LINE = 16, PAGE = 800;
/** Zoom per wheel pixel: a mouse notch (100 px) is 2^0.2 ≈ 15 %; a pinch sends small deltas and gets five times as much. */
const PER_PIXEL = 0.002, PER_PINCH_PIXEL = 0.01;
const STEPS = [0.05, 0.1, 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

const clampZoom = (zoom: number) => Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, zoom));

/** Zoom to `zoom`, keeping the board point under `at` (canvas px) where it is. */
export function zoomAt(v: Viewport, zoom: number, at: { x: number; y: number }): Viewport {
  const next = clampZoom(zoom), ratio = next / v.zoom;
  return { x: at.x - (at.x - v.x) * ratio, y: at.y - (at.y - v.y) * ratio, zoom: next };
}

/** A wheel or trackpad event: Ctrl, ⌘ or a pinch (which browsers send as Ctrl + wheel) zooms at the cursor, anything else pans. */
export function wheelViewport(v: Viewport, event: WheelInput, at: { x: number; y: number }): Viewport {
  const unit = event.deltaMode === 1 ? LINE : event.deltaMode === 2 ? PAGE : 1;
  if (event.ctrlKey || event.metaKey) {
    const pinch = event.deltaMode === 0 && Math.abs(event.deltaY) < 50;
    return zoomAt(v, v.zoom * 2 ** (-event.deltaY * unit * (pinch ? PER_PINCH_PIXEL : PER_PIXEL)), at);
  }
  // Shift turns a plain mouse wheel sideways (a trackpad already sends deltaX).
  const sideways = event.shiftKey && !event.deltaX;
  const dx = (sideways ? event.deltaY : event.deltaX) * unit, dy = sideways ? 0 : event.deltaY * unit;
  return { x: v.x - dx, y: v.y - dy, zoom: v.zoom };
}

/** The objects (a card's page) of the live items overlapping `area` (board coordinates), each once in lower case: the view
 * session's viewport, which gets their changes and presence in detail. */
export function visibleObjects(items: readonly BoardItem[], area: Geometry): string[] {
  const ids = new Set<string>();
  for (const { targetResourceId, deleted, x, y, width, height } of items) {
    if (targetResourceId && !deleted && x < area.x + area.width && x + width > area.x && y < area.y + area.height && y + height > area.y) ids.add(targetResourceId.toLowerCase());
  }
  return [...ids];
}

/** The next round zoom level up or down from `zoom` (the − and + buttons). */
export function zoomStep(zoom: number, direction: 1 | -1): number {
  const next = direction > 0 ? STEPS.find(step => step > zoom + 1e-6) : [...STEPS].reverse().find(step => step < zoom - 1e-6);
  return next ?? (direction > 0 ? MAX_ZOOM : MIN_ZOOM);
}

/** The view that shows `bounds` whole with a margin, centred, at most at `maxZoom` (100 % when a board opens, spec §2). */
export function fitViewport(bounds: Geometry | null, size: { width: number; height: number }, options: { maxZoom?: number; minZoom?: number; padding?: number } = {}): Viewport {
  const padding = options.padding ?? 48;
  if (!bounds) return { x: size.width / 2, y: size.height / 2, zoom: 1 };
  const zoom = clampZoom(Math.max(options.minZoom ?? 0, Math.min((size.width - 2 * padding) / Math.max(bounds.width, 1), (size.height - 2 * padding) / Math.max(bounds.height, 1), options.maxZoom ?? 1)));
  return { x: size.width / 2 - (bounds.x + bounds.width / 2) * zoom, y: size.height / 2 - (bounds.y + bounds.height / 2) * zoom, zoom };
}

/** A CSS cubic-bezier() as a function of progress, for d3's transitions of the view (`--atlas-ease-travel` is (.3, .2, .1, 1)). */
export function cubicBezier(x1: number, y1: number, x2: number, y2: number): (t: number) => number {
  const at = (s: number, a: number, b: number) => 3 * (1 - s) * (1 - s) * s * a + 3 * (1 - s) * s * s * b + s * s * s;
  return t => {
    if (t <= 0) return 0;
    if (t >= 1) return 1;
    let lo = 0, hi = 1;
    for (let step = 0; step < 24; step++) { const mid = (lo + hi) / 2; if (at(mid, x1, x2) < t) lo = mid; else hi = mid; }
    return at((lo + hi) / 2, y1, y2);
  };
}
