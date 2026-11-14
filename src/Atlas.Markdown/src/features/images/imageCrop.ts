import type { ImagePresentation } from './presentation'

export type CropCorner = 'top-left' | 'top-right' | 'bottom-left' | 'bottom-right'
type Edge = 'cropLeft' | 'cropTop' | 'cropRight' | 'cropBottom'
/** Screen edges in clockwise order, after the same flip/rotation used by the renderer. */
export function cropScreenEdges(p: ImagePresentation): Edge[] {
  const edges: Edge[] = p.flip
    ? ['cropRight', 'cropTop', 'cropLeft', 'cropBottom']
    : ['cropLeft', 'cropTop', 'cropRight', 'cropBottom']
  const turns = p.rotate / 90
  return edges.map((_, i) => edges[(i - turns + 4) % 4]!)
}
export function imageCropInsets(p: ImagePresentation) {
  const [left, top, right, bottom] = cropScreenEdges(p).map(edge => p[edge])
  return { left: left!, top: top!, right: right!, bottom: bottom! }
}
/** Deltas are percentages of the full, transformed source; retain at least 6% per axis. */
export function moveImageCropCorner(p: ImagePresentation, corner: CropCorner, dx: number, dy: number): ImagePresentation {
  const edges = cropScreenEdges(p)
  const next = { ...p }
  const horizontal = corner.endsWith('left') ? 0 : 2
  const vertical = corner.startsWith('top') ? 1 : 3
  for (const [index, delta] of [[horizontal, dx], [vertical, dy]] as const) {
    const edge = edges[index]!
    const opposite = edges[(index + 2) % 4]!
    next[edge] = Math.max(0, Math.min(90, 94 - p[opposite], p[edge] + delta * (index < 2 ? 1 : -1)))
  }
  return next
}
