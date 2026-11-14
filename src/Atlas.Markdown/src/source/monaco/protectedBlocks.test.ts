import { describe, expect, it } from 'vitest'
import { applySourceEdits, createProtectedFenceGuard, type SourceEdit } from './protectedBlocks'
import { protectedFence } from '../protectedFence'

const { checkProtectedEdit, findProtectedRanges, resolveKnownBlocks, scanFences, shortLabel } = createProtectedFenceGuard(protectedFence)

// Phase 0 regression harness for the guard (docs/page-source-editor.md). Monaco cannot run in jsdom, so
// every user edit path is expressed as the edit operations Monaco hands to `model.pushEditOperations`
// (see e.g. contrib/linesOperations/browser/moveLinesCommand.js); tests/e2e/page-source.spec.ts drives the
// real editor.
const lines = [
  '# Title', // 1
  '', // 2
  '```md-protected b3', // 3
  'Chráněný blok: Graf „Tržby 2025“', // 4
  '```', // 5
  '', // 6
  'Paragraph with Graf text', // 7
  '', // 8
  '```md-protected b4', // 9
  'Chráněný blok: Tabulka', // 10
  '```', // 11
  'tail', // 12
]
const known = resolveKnownBlocks(lines, [{ id: 'b3' }, { id: 'b4' }])
const ranges = findProtectedRanges(lines, known)
const edit = (startLine: number, startColumn: number, endLine: number, endColumn: number, text: string | null = ''): SourceEdit => ({ startLine, startColumn, endLine, endColumn, text })
const insert = (line: number, column: number, text: string) => edit(line, column, line, column, text)
const check = (...edits: SourceEdit[]) => checkProtectedEdit(lines, known, ranges, edits)
const rejected = (id: string, ...edits: SourceEdit[]) => {
  const verdict = check(...edits)
  expect(verdict.allowed).toBe(false)
  expect(!verdict.allowed && verdict.block.id).toBe(id)
}
const allowed = (...edits: SourceEdit[]) => expect(check(...edits)).toEqual({ allowed: true })

describe('protected block scanning', () => {
  it('finds complete md-protected fences at column 0 and labels them from the fence', () => {
    expect(scanFences(lines).map(fence => [fence.id, fence.startLine, fence.endLine])).toEqual([['b3', 3, 5], ['b4', 9, 11]])
    expect(ranges).toEqual([
      { id: 'b3', label: 'Chráněný blok: Graf „Tržby 2025“', startLine: 3, endLine: 5 },
      { id: 'b4', label: 'Chráněný blok: Tabulka', startLine: 9, endLine: 11 },
    ])
    expect(shortLabel(ranges[0]!.label)).toBe('Graf „Tržby 2025“')
  })

  it('ignores indented, unterminated and unknown fences', () => {
    const text = ['  ```md-protected b3', 'x', '```', '```md-protected zz', 'y', '```', '```md-protected b4', 'never closed']
    expect(findProtectedRanges(text, known)).toEqual([])
    expect(scanFences(text).map(fence => fence.id)).toEqual(['zz'])
  })

  it('re-protects a known id only while its body is intact (undo/redo restore it)', () => {
    const damaged = [...lines]; damaged[3] = 'Chráněný blok: Graf „Tržby 2026“'
    expect(findProtectedRanges(damaged, known).map(range => range.id)).toEqual(['b4'])
    expect(findProtectedRanges(lines, known).map(range => range.id)).toEqual(['b3', 'b4'])
  })

  it('prefers the line hint, and protects a block absent from the text once it is pasted back', () => {
    const blocks = resolveKnownBlocks(['text'], [{ id: 'b9', label: 'Chráněný blok: Obrázek' }])
    expect(findProtectedRanges(['text'], blocks)).toEqual([])
    expect(findProtectedRanges(['text', '', '```md-protected b9', 'Chráněný blok: Obrázek', '```'], blocks)).toEqual([
      { id: 'b9', label: 'Chráněný blok: Obrázek', startLine: 3, endLine: 5 },
    ])
    const twice = [...lines.slice(0, 6), ...lines.slice(2, 5)]
    expect(resolveKnownBlocks(twice, [{ id: 'b3', startLine: 7, endLine: 9 }]).get('b3')!.lines).toEqual(lines.slice(2, 5))
  })

  it('reads fences with the session grammar: trailing blanks allowed, only session ids', () => {
    const spaced = [...lines]; spaced[2] = '```md-protected b3 \t'; spaced[4] = '```  '
    expect(scanFences(spaced).map(fence => fence.id)).toEqual(['b3', 'b4'])
    expect(findProtectedRanges(spaced, known).map(range => range.id)).toEqual(['b3', 'b4'])
    const invalid = ['```md-protected b3.x', 'Chráněný blok: X', '```', `\`\`\`md-protected b${'1'.repeat(64)}`, 'Chráněný blok: Y', '```']
    expect(scanFences(invalid)).toEqual([])
    expect(findProtectedRanges(invalid, resolveKnownBlocks(invalid, [{ id: 'b3.x', label: 'Chráněný blok: X' }]))).toEqual([])
  })

  it('protects every intact copy of a duplicated block (the session reports the duplicate)', () => {
    const duplicated = [...lines, '', ...lines.slice(2, 5)]
    expect(findProtectedRanges(duplicated, known).map(range => [range.id, range.startLine])).toEqual([['b3', 3], ['b4', 9], ['b3', 14]])
  })
})

describe('protected block guard', () => {
  it('passes edits that touch no block', () => {
    allowed(insert(7, 1, 'x'))
    allowed(edit(6, 1, 7, 1)) // deleting the empty line after a block as a whole line
    allowed(insert(8, 1, '```md-protected b3\nChráněný blok: Graf „Tržby 2025“\n```\n')) // pasting a whole copy
  })

  it('rejects typing inside a block, including IME/typing at the fence edges', () => {
    rejected('b3', insert(4, 5, 'x'))
    rejected('b3', edit(4, 1, 4, 3, 'Xy')) // IME composition replacing characters
    rejected('b3', insert(3, 1, 'x'))
    rejected('b3', insert(5, 4, 'x'))
    rejected('b4', edit(10, 17, 10, 18)) // Backspace inside the label
  })

  it('allows Enter before the opening fence and after the closing fence', () => {
    allowed(insert(3, 1, '\n'))
    allowed(insert(5, 4, '\n'))
  })

  it('rejects a paste over part of a block and allows one over the whole block', () => {
    rejected('b3', edit(2, 1, 4, 3, 'pasted'))
    rejected('b3', edit(4, 1, 7, 1, 'pasted\n'))
    allowed(edit(3, 1, 6, 1, 'pasted\n'))
    allowed(edit(2, 1, 5, 4, 'pasted')) // selection ending at the end of the closing fence
  })

  it('allows cutting a whole block and rejects cutting part of it', () => {
    allowed(edit(3, 1, 6, 1))
    allowed(edit(2, 1, 8, 1))
    rejected('b3', edit(3, 1, 4, 1)) // opening fence line only (Ctrl+X on an empty selection)
    rejected('b3', edit(4, 1, 6, 1)) // label and closing fence
  })

  it('allows deleting an adjacent empty line and rejects joining text onto a fence', () => {
    allowed(edit(2, 1, 3, 1)) // Delete on the empty line before
    allowed(edit(5, 4, 6, 1)) // Delete at the end of the closing fence joins the empty line after
    allowed(edit(8, 1, 9, 1)) // Backspace at the start of an opening fence after an empty line
    rejected('b3', edit(1, 8, 3, 1)) // joining "# Title" onto the opening fence
    rejected('b4', edit(11, 4, 12, 1)) // joining "tail" onto the closing fence
  })

  it('allows moving lines with the whole block selected and rejects moving a line into a block', () => {
    // Alt+Down with lines 3–5 selected (MoveLinesCommand): the next line is deleted and re-inserted above.
    allowed(edit(5, 4, 6, 1, null), insert(3, 1, '\n'))
    // Alt+Up with lines 3–5 selected: the previous line (empty) moves below the block.
    allowed(edit(1, 8, 2, 1, null), insert(5, 4, '\n'))
    // Alt+Up on the empty line 6: the closing fence line moves below it.
    rejected('b3', insert(6, 1, '```'), edit(5, 1, 5, 4, null))
    // Alt+Down on "Paragraph" (line 7) twice would push it into b4: the first step swaps with the empty line 8.
    allowed(edit(7, 25, 8, 1, null), insert(7, 1, '\n'))
  })

  it('rejects a multi-cursor edit when any cursor touches a block', () => {
    rejected('b3', insert(1, 1, 'x'), insert(4, 1, 'x'), insert(7, 1, 'x'))
    allowed(insert(1, 1, 'x'), insert(7, 1, 'x'))
  })

  it('rejects the whole replace-all when one match is inside a block', () => {
    rejected('b3', edit(4, 16, 4, 20, 'Chart'), edit(7, 16, 7, 20, 'Chart'))
    allowed(edit(7, 16, 7, 20, 'Chart'), edit(1, 3, 1, 8, 'Nadpis'))
  })

  it('allows replacing a block by the same block and cut + paste to another place in one step', () => {
    allowed(edit(3, 1, 5, 4, '```md-protected b3\nChráněný blok: Graf „Tržby 2025“\n```'))
    allowed(edit(3, 1, 7, 1), insert(8, 1, '```md-protected b3\nChráněný blok: Graf „Tržby 2025“\n```\n\n'))
  })

  it('rejects leaving a damaged copy even when an intact copy is pasted elsewhere', () => {
    rejected('b3', edit(4, 1, 4, 3, 'X'), insert(8, 1, '```md-protected b3\nChráněný blok: Graf „Tržby 2025“\n```\n'))
  })

  it('treats trailing blanks after a fence as the session does', () => {
    allowed(insert(3, lines[2]!.length + 1, ' '), insert(5, 4, '\t'))
    rejected('b3', edit(3, 1, 5, 4, '```md-protected b3 \nChráněný blok: Graf „Zisk“\n```')) // a damaged copy the session reads as b3
  })

  it('normalizes CRLF edit text', () => {
    allowed(insert(5, 4, '\r\n'))
    expect(applySourceEdits(['a', 'b'], [insert(1, 2, '\r\nx')])).toEqual(['a', 'x', 'b'])
  })
})

describe('shared fence syntax', () => {
  it('follows the session rules: trailing blanks allowed, ids limited to [A-Za-z0-9_-]{1,64}', () => {
    expect(scanFences(['```md-protected b3  ', 'Chráněný blok: X', '```\t']).map(fence => fence.id)).toEqual(['b3'])
    expect(scanFences(['```md-protected b.3', 'Chráněný blok: X', '```'])).toEqual([])
    expect(scanFences([`\`\`\`md-protected ${'a'.repeat(65)}`, 'Chráněný blok: X', '```'])).toEqual([])
    expect(shortLabel('Chráněný blok: Graf')).toBe('Graf')
  })
})
