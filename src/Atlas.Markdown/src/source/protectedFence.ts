// The protected-fence syntax (docs/page-source-editor.md), shared by the headless session (session.ts)
// and the Monaco guard (monaco/protectedBlocks.ts) so both accept exactly the same text. Constants only,
// no imports, a tiny lazy chunk: the session imports it, and `mountSourceEditor` loads it next to the
// Monaco chunk and hands `protectedFence` to the editor. The Monaco guard must not import it at runtime:
// the Monaco chunk group would absorb it and the session would then download Monaco.
//
//     ```md-protected <id>
//     Chráněný blok: Graf „Tržby 2025“
//     …the block's own text as Markdown, to read and search; never read back…
//     ```

/** A character of a protected block id (the session generates `b` + lowercase base 36). */
export const PROTECTED_ID_CHAR = '[A-Za-z0-9_-]'
/** A protected block id: both sides accept this pattern. */
export const PROTECTED_ID_PATTERN = `${PROTECTED_ID_CHAR}{1,64}`
/** Opening fence at column 0; trailing spaces and tabs are tolerated. Group 1 is the id. */
export const PROTECTED_OPEN = new RegExp(`^\`\`\`md-protected (${PROTECTED_ID_PATTERN})[ \t]*$`)
/** Closing fence at column 0; trailing spaces and tabs are tolerated. */
export const PROTECTED_CLOSE = /^```[ \t]*$/
/** Every label line the session writes starts with this prefix. */
export const PROTECTED_LABEL_PREFIX = 'Chráněný blok: '

/**
 * The lines of a protected block: the fence, the label and the block's text (a view of the original node, which
 * `apply` puts back untouched). A text line that starts a fence is written with tildes, so it cannot close the block.
 */
export function protectedFenceLines(id: string, label: string, text = ''): string[] {
  const body = text ? text.split(/\r\n?|\n/).map(line => line.replace(/^(\s*)(`{3,})/, (_match, indent: string, ticks: string) => indent + '~'.repeat(ticks.length))) : []
  return [`\`\`\`md-protected ${id}`, ...label.split(/\r\n?|\n/), ...body, '```']
}

const withoutTrailingBlanks = (line: string) => line.replace(/[ \t]+$/, '')
/** Whether a block line still reads as `expected` for the session, which ignores trailing blanks. */
export function sameProtectedLine(line: string, expected: string): boolean {
  return withoutTrailingBlanks(line) === withoutTrailingBlanks(expected)
}

export interface ProtectedFenceSyntax {
  readonly open: RegExp
  readonly close: RegExp
  readonly labelPrefix: string
  lines(id: string, label: string, text?: string): string[]
  /** Compares a block line with its expected text the way the session reads it (trailing blanks aside). */
  sameLine(line: string, expected: string): boolean
}

/** The syntax as one value, for the lazy Monaco guard. */
export const protectedFence: ProtectedFenceSyntax = Object.freeze({
  open: PROTECTED_OPEN, close: PROTECTED_CLOSE, labelPrefix: PROTECTED_LABEL_PREFIX, lines: protectedFenceLines, sameLine: sameProtectedLine,
})
