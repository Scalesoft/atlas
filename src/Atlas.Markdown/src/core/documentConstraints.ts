import type { Editor } from '@tiptap/core'
import type { MarkdownDocumentSchema, MarkdownProfile } from './types'

export const MAX_TABLE_CELLS = 1_000
export const MAX_TABLE_SPAN = 1_000
// ProseMirror tables use 0 as an "unknown column" sentinel inside merged cells.
export const MIN_TABLE_COLUMN_WIDTH = 0
export const MAX_TABLE_COLUMN_WIDTH = 10_000
export const MIN_TABLE_ROW_HEIGHT = 24
export const MAX_TABLE_ROW_HEIGHT = 2_000
/**
 * The largest Markdown text accepted as input: a paste, "paste as Markdown", a dropped `.md` file and
 * the page source (5 MiB; string length for text, byte size for files).
 */
export const MAX_MARKDOWN_TEXT_LENGTH = 5 * 1024 * 1024
/**
 * The built-in content schema revision a controller declares for collaboration (docs/collaboration-schema.md).
 * Rich revision 3 adds heading level 4 and the `caution` callout variant; revision 4 lets inline code combine with the other
 * marks (bold, italic, strike, highlight, underline, link); revision 5 adds the timed list (timedList, timedItem);
 * revision 6 made a disclosure's title its first child `disclosureTitle`; revision 7 has no disclosure (a heading folds, the content repair
 * turns a disclosure into a heading and its body). Markdown-safe is unchanged.
 */
export function builtinSchemaRevision(profile: MarkdownProfile): MarkdownDocumentSchema['version'] {
  return profile === 'rich' ? 7 : 1
}
/** Heading levels H1–H6, the full Markdown range. */
export const HEADING_LEVELS = [1, 2, 3, 4, 5, 6] as const
/** The rel every link that opens a new tab renders; the stored rel is never read (reverse tabnabbing). */
export const LINK_REL = 'noopener noreferrer'

const cellColors = new Set(['accent', 'green', 'amber', 'red'])
const alignments = new Set(['left', 'center', 'right'])

export function isValidTableSize(rows: unknown, columns: unknown): boolean {
  return Number.isInteger(rows) && Number.isInteger(columns)
    && (rows as number) > 0 && (columns as number) > 0
    && (rows as number) <= MAX_TABLE_SPAN && (columns as number) <= MAX_TABLE_SPAN
    && (rows as number) * (columns as number) <= MAX_TABLE_CELLS
}

export function isValidTableSpan(value: unknown): value is number {
  return Number.isInteger(value) && (value as number) >= 1 && (value as number) <= MAX_TABLE_SPAN
}

export function isValidColumnWidths(value: unknown, colspan: number): value is null | readonly number[] {
  return value === null || (Array.isArray(value) && value.length === colspan
    && value.every(width => Number.isFinite(width)
      && width >= MIN_TABLE_COLUMN_WIDTH && width <= MAX_TABLE_COLUMN_WIDTH))
}

export function isValidRowHeight(value: unknown): value is null | number {
  return value === null || (Number.isFinite(value)
    && (value as number) >= MIN_TABLE_ROW_HEIGHT && (value as number) <= MAX_TABLE_ROW_HEIGHT)
}

export function isValidCellColor(value: unknown): value is null | string {
  return value === null || (typeof value === 'string' && cellColors.has(value))
}

export function isValidAlignment(value: unknown): value is null | string {
  return value === null || (typeof value === 'string' && alignments.has(value))
}

export function canGrowSelectedTable(editor: Editor, axis: 'row' | 'column'): boolean {
  const position = editor.state.selection.$from
  for (let depth = position.depth; depth > 0; depth--) {
    const table = position.node(depth)
    if (table.type.name !== 'table') continue
    const rows = table.childCount
    let columns = 0
    table.firstChild?.forEach(cell => { columns += Number(cell.attrs.colspan) || 1 })
    return axis === 'row' ? isValidTableSize(rows + 1, columns) : isValidTableSize(rows, columns + 1)
  }
  return false
}
