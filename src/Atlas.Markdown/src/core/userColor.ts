// Atlas Markdown — the colour of a collaborator on every surface (caret, selection, avatar, pointer).
//
// ONE palette (collaboratorColors.json, also embedded by Atlas.Markdown.Sync, whose server assigns the colour per live session):
// the 12 hues of the appearance catalog in an order where neighbours differ clearly, each with a light and a dark variant. The server
// stamps the light variant as a literal "#rrggbb" (y-tiptap appends an alpha to it for selections); `collaboratorColor` swaps it for
// the dark variant on a dark surface. Literal values, not `var(--…)`: the caret DOM lives outside a component scope where tokens resolve.
import palette from './collaboratorColors.json'

export interface CollaboratorColor { id: string; light: string; dark: string }

/** The palette in assignment order. */
export const COLLABORATOR_COLORS: readonly CollaboratorColor[] = palette

/** The slot a person without a chosen colour starts from: a hash of the user id (the server's `CollaboratorPalette.Auto`, same arithmetic). */
export function markdownUserColorSlot(userId: string): number {
  let hash = 0
  for (let i = 0; i < userId.length; i++) hash = (Math.imul(hash, 31) + userId.charCodeAt(i)) | 0
  return Math.abs(hash) % COLLABORATOR_COLORS.length
}

/** The automatic colour of a user id (light variant): same id → same colour everywhere, until the server assigns another. */
export function markdownUserColor(userId: string): string {
  return COLLABORATOR_COLORS[markdownUserColorSlot(userId)].light
}

/** The variant of a stamped colour for the surface; a colour outside the palette (an agent's, a host's own) stays as it is. */
export function collaboratorColor(color: string, dark: boolean): string {
  const entry = COLLABORATOR_COLORS.find(item => item.light === color || item.dark === color)
  return entry ? (dark ? entry.dark : entry.light) : color
}

/** The full palette's light variants, for the picker and the legend. */
export const MARKDOWN_USER_PALETTE: readonly string[] = COLLABORATOR_COLORS.map(item => item.light)
