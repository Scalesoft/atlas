/**
 * The adaptive dot grid behind a board (interaction spec §2). World spacing is 24 · 4^k; the level k is the one whose dots lie
 * 12 to 48 px apart on screen. The level four times coarser is drawn over it at full strength, and the fine level fades in
 * from 12 to 24 px, so a zoom across a level boundary changes nothing visible: just above it the new fine level is still
 * transparent, and the coarse level is the fine level of just below. Drawn in an opaque colour, the coarse dots that lie on fine
 * ones do not darken them. The dot itself has a constant size on screen (the caller draws it).
 */
export interface GridLevel {
  /** World units between two dots. */
  spacing: number;
  /** Screen pixels between two dots: the size of the pattern tile. */
  size: number;
  /** Where the pattern starts on screen, within one tile. */
  x: number;
  y: number;
  opacity: number;
}

const BASE = 24, MIN = 12, FULL = 24;

const tile = (value: number, size: number) => ((value % size) + size) % size;
const smoothstep = (t: number) => { const c = Math.min(1, Math.max(0, t)); return c * c * (3 - 2 * c); };

export function gridLevels(viewport: { x: number; y: number; zoom: number }): GridLevel[] {
  const { zoom } = viewport;
  let k = Math.ceil(Math.log(MIN / (BASE * zoom)) / Math.log(4));
  // Rounding of the logarithm at an exact boundary: settle on the level that is really inside ⟨12; 48).
  while (BASE * 4 ** k * zoom < MIN) k++;
  while (BASE * 4 ** (k - 1) * zoom >= MIN) k--;
  return [k, k + 1].map((level, index) => {
    const spacing = BASE * 4 ** level, size = spacing * zoom;
    return { spacing, size, x: tile(viewport.x, size), y: tile(viewport.y, size), opacity: index ? 1 : smoothstep((size - MIN) / (FULL - MIN)) };
  });
}
