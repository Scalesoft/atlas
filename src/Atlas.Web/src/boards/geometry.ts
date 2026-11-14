import type { Geometry } from "./boardApi";

/** The four numbers of a box, nothing else an item carries. */
export const geometryOf = ({ x, y, width, height }: Geometry): Geometry => ({ x, y, width, height });

/** The middle of a box. */
export const centreOf = (g: Geometry) => ({ x: g.x + g.width / 2, y: g.y + g.height / 2 });

/** The smallest box around all of them (at least one). */
export const boundsOf = (boxes: readonly Geometry[]): Geometry => {
  const x = Math.min(...boxes.map(b => b.x)), y = Math.min(...boxes.map(b => b.y));
  return { x, y, width: Math.max(...boxes.map(b => b.x + b.width)) - x, height: Math.max(...boxes.map(b => b.y + b.height)) - y };
};
