// Atlas Markdown — the colour of a collaborator on every surface (caret, selection, avatar, pointer).
//
// ONE palette (collaboratorColors.json, also embedded by Atlas.Markdown.Sync, whose server assigns the colour per live session):
// the 12 hues of the appearance catalog in an order where neighbours differ clearly, each with a light and a dark variant. The server
// stamps the light variant as a literal "#rrggbb"; `collaboratorColor` gives either variant (carets carry both, collaboration.css picks
// by the surface). Literal values, not `var(--…)`: the caret DOM lives outside a component scope where tokens resolve. Also the light
// subpath `@atlas/markdown/core/userColor` (also the initials rule `markdownInitials`), for a host's first screen that must not load the editor.
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

/**
 * The initials of a person on every surface (avatar, presence, assist rows): the first letters of the first two words of the name,
 * Markdown stripped (every name is inline Markdown); one word gives one letter, nothing readable gives ''.
 */
export function markdownInitials(name: string | null | undefined, locale?: string): string {
  const plain = (name ?? '')
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/(\*\*|__|~~)(?=\S)(.+?)(?<=\S)\1/g, '$2')
    .replace(/(?<![\w\\*])([*_])(?=\S)(.+?)(?<=\S)\1(?![\w*])/g, '$2')
    .replace(/`([^`]+)`/g, '$1')
    .replace(/\\([\\`*_{}[\]()#+.!|<>~-])/g, '$1')
  return plain.split(/\s+/).filter(Boolean).slice(0, 2).map(word => Array.from(word)[0]).join('').toLocaleUpperCase(locale)
}
