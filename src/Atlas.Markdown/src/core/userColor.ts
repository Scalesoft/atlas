// Atlas Markdown — stable per-user avatar/caret/presence colour.
//
// CollaborationCaret injects the colour as an inline style on the remote caret
// and selection, so these must be literal colour values (not `var(--…)` — the
// caret DOM lives outside a component scope where our tokens resolve). The
// palette is a fixed 8-colour ramp picked for contrast on both light and dark
// editor surfaces; a deterministic hash of the userId keeps a given user the
// same colour across sessions and across every peer's screen.

const PALETTE: readonly string[] = [
  '#e11d48', // rose
  '#db2777', // pink
  '#7c3aed', // violet
  '#2563eb', // blue
  '#07809d', // cyan
  '#04845c', // emerald
  '#9e6c03', // amber
  '#c94c0a', // orange
]

/** Deterministic colour for a user id — same id → same colour everywhere. */
export function markdownUserColor(userId: string): string {
  let hash = 0
  for (let i = 0; i < userId.length; i++) {
    hash = (Math.imul(hash, 31) + userId.charCodeAt(i)) | 0
  }
  return PALETTE[Math.abs(hash) % PALETTE.length]
}

/** The full palette, exposed for the gallery/presence legend. */
export const MARKDOWN_USER_PALETTE = PALETTE

