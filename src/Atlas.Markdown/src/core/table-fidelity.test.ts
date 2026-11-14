// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import { Editor } from '@tiptap/core'
import { createMarkdownEditorController, type MarkdownCommand, type MarkdownRichValue } from './index'
import { createMarkdownKit } from '../index'

const seed = '| Name | Status |\n| --- | --- |\n| Alpha | Ready |\n| Beta | Waiting |'
const cleanups: (() => void)[] = []
afterEach(() => { cleanups.splice(0).forEach(cleanup => cleanup()); document.body.replaceChildren() })

function mount(value: string | MarkdownRichValue = seed) {
  const controller = createMarkdownEditorController({
    profile: typeof value === 'string' ? 'markdown-safe' : 'rich', source: { kind: 'local', value },
  })
  cleanups.push(() => controller.destroy())
  const host = document.createElement('div')
  document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Table fidelity' })
  return { controller, host }
}

function nextCell(host: HTMLElement) {
  host.querySelector('.ProseMirror')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }))
}

describe('table fidelity at public editor boundaries', () => {
  it.each(['toggle-header-row', 'toggle-header-column'] as const)('disables %s in markdown-safe without changing the saved table', type => {
    const { controller } = mount()
    expect(controller.query({ type }).enabled).toBe(false)
    expect(controller.dispatch({ type })).toBe(false)
    expect(controller.getValue()).toBe(seed + '\n')
  })

  it('guards the legacy direct header-cell command too', () => {
    const editor = new Editor({ content: seed, extensions: createMarkdownKit({ profile: 'markdown-safe' }).extensions })
    cleanups.push(() => editor.destroy())
    editor.commands.goToNextCell()
    editor.commands.goToNextCell()
    expect(editor.can().toggleHeaderCell()).toBe(false)
    expect(editor.commands.toggleHeaderCell()).toBe(false)
    expect((editor.getJSON() as any).content?.[0].content?.[1].content?.[0].type).toBe('tableCell')
  })

  it('does not normalize or mutate a readonly table when querying or dispatching commands', () => {
    const { controller } = mount()
    controller.setEditable(false)
    const revision = controller.state.contentRevision
    for (const command of [
      { type: 'set-alignment', alignment: 'center' },
      { type: 'add-row', side: 'before' }, { type: 'delete-row' },
      { type: 'add-column', side: 'after' }, { type: 'delete-column' },
    ] satisfies MarkdownCommand[]) {
      expect(controller.query(command).enabled).toBe(false)
      expect(controller.dispatch(command)).toBe(false)
    }
    expect(controller.state.contentRevision).toBe(revision)
    expect(controller.getValue()).toBe(seed + '\n')
  })

  it.each([
    ['left', ':---'], ['center', ':---:'], ['right', '---:'],
  ] as const)('roundtrips %s alignment from a body cell as a whole GFM column', (alignment, delimiter) => {
    const { controller, host } = mount()
    nextCell(host); nextCell(host)
    expect(controller.dispatch({ type: 'set-alignment', alignment })).toBe(true)
    expect(Array.from(host.querySelectorAll('tr > :first-child')).map(cell => (cell as HTMLElement).style.textAlign)).toEqual([alignment, alignment, alignment])
    const projection = controller.getProjection()
    expect(projection.markdown).toContain(`| ${delimiter} | --- |`)
    expect(projection.markdownLossy).toBe(false)
    const reloaded = mount(projection.markdown)
    expect(Array.from(reloaded.host.querySelectorAll('tr > :first-child')).map(cell => (cell as HTMLElement).style.textAlign)).toEqual([alignment, alignment, alignment])
    expect(reloaded.controller.getValue()).toBe(projection.markdown)
  })

  it.each([
    [{ type: 'add-row', side: 'before' }, 'NameStatusAlphaReadyBetaWaiting'],
    [{ type: 'add-row', side: 'after' }, 'NameStatusAlphaReadyBetaWaiting'],
    [{ type: 'delete-row' }, 'AlphaReadyBetaWaiting'],
    [{ type: 'add-column', side: 'before' }, 'NameStatusAlphaReadyBetaWaiting'],
    [{ type: 'add-column', side: 'after' }, 'NameStatusAlphaReadyBetaWaiting'],
    [{ type: 'delete-column' }, 'StatusReadyWaiting'],
  ] satisfies [MarkdownCommand, string][])('keeps a single header row through structural operation %j', (command, expectedText) => {
    const { controller, host } = mount()
    controller.dispatch({ type: 'set-alignment', alignment: 'center' })
    expect(controller.dispatch(command)).toBe(true)
    const rows = Array.from(host.querySelectorAll('tr'))
    expect(rows[0].querySelectorAll('td')).toHaveLength(0)
    expect(rows.slice(1).flatMap(row => Array.from(row.querySelectorAll('th')))).toHaveLength(0)
    const projection = controller.getProjection()
    expect(projection.markdownLossy).toBe(false)
    expect(projection.markdown).not.toContain('[table]')
    expect(host.querySelector('table')?.textContent).toBe(expectedText)
    const reloaded = mount(projection.markdown)
    expect(reloaded.host.querySelector('table')?.textContent).toBe(host.querySelector('table')?.textContent)
    expect(Array.from(reloaded.host.querySelectorAll('td, th')).map(cell => (cell as HTMLElement).style.textAlign)).toEqual(Array.from(host.querySelectorAll('td, th')).map(cell => (cell as HTMLElement).style.textAlign))
  })

  it('roundtrips literal pipes and inline code inside GFM cells', () => {
    const { controller } = mount('| Name | Value |\n| --- | --- |\n| A\\|B | `C\\|D` |')
    const reloaded = mount(controller.getProjection().markdown)
    expect(Array.from(reloaded.host.querySelectorAll('td')).map(cell => cell.textContent)).toEqual(['A|B', 'C|D'])
    expect(reloaded.host.querySelector('code')?.textContent).toBe('C|D')
  })

  it('retains rich per-cell alignment while marking its Markdown projection lossy', () => {
    const source = mount().controller.getProjection().rich
    const { controller, host } = mount(source)
    nextCell(host); nextCell(host)
    expect(controller.dispatch({ type: 'set-alignment', alignment: 'right' })).toBe(true)
    expect(Array.from(host.querySelectorAll('tr > :first-child')).map(cell => (cell as HTMLElement).style.textAlign)).toEqual(['', 'right', ''])
    const projection = controller.getProjection()
    expect(projection.markdownLossy).toBe(true)
    expect(mount(projection.rich).host.querySelector('td')?.getAttribute('data-text-align')).toBe('right')
    expect(mount(projection.markdown).host.querySelector('table')?.textContent).toBe('NameStatusAlphaReadyBetaWaiting')
  })

  it('exports every cell text from a rich irregular header table instead of a placeholder', () => {
    const { controller } = mount(mount().controller.getProjection().rich)
    expect(controller.dispatch({ type: 'toggle-header-column' })).toBe(true)
    const projection = controller.getProjection()
    expect(projection.markdownLossy).toBe(true)
    for (const text of ['Name', 'Status', 'Alpha', 'Ready', 'Beta', 'Waiting']) expect(projection.markdown).toContain(text)
    expect(mount(projection.markdown).host.textContent).toContain('Alpha')
    expect(mount(controller.getValue()).controller.getProjection().rich).toEqual(projection.rich)
  })

  it('writes a rich merged cell with all its paragraphs as the dialect a-table', () => {
    const paragraph = (text: string) => ({ type: 'paragraph', content: [{ type: 'text', text }] })
    const { controller } = mount({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{
      type: 'table', content: [
        { type: 'tableRow', content: [{ type: 'tableHeader', attrs: { colspan: 2 }, content: [paragraph('Merged title'), paragraph('Second paragraph')] }] },
        { type: 'tableRow', content: [{ type: 'tableCell', content: [paragraph('Left')] }, { type: 'tableCell', content: [paragraph('Right')] }] },
      ],
    }] } })
    const projection = controller.getProjection()
    expect(projection.markdownLossy).toBe(true)
    // Read back in markdownDialect.test.ts; markdown-safe holds no merged cell, so it is not reloaded here.
    expect(projection.markdown).toBe([
      '```a-table', '- cells:', '    - header: true', '      colspan: 2', '      content: |-', '        Merged title', '', '        Second paragraph',
      '- cells:', '    - content: Left', '    - content: Right', '```',
    ].join('\n'))
  })

  it.each([
    [[{ type: 'text', text: 'Before' }, { type: 'hardBreak' }, { type: 'text', text: 'After' }], false],
    [[{ type: 'text', text: 'Before\nAfter' }], true],
  ] as const)('preserves multiline cell content and reports projection loss honestly: %j', (content, lossy) => {
    const { controller } = mount({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{
      type: 'table', content: [
        { type: 'tableRow', content: [{ type: 'tableHeader', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Heading' }] }] }] },
        { type: 'tableRow', content: [{ type: 'tableCell', content: [{ type: 'paragraph', content }] }] },
      ],
    }] } })
    const projection = controller.getProjection()
    expect(projection.markdown).toContain('| Before<br>After |')
    expect(projection.markdownLossy).toBe(lossy)
    const reloaded = mount(projection.markdown)
    expect(reloaded.host.querySelectorAll('tr')).toHaveLength(2)
    expect(reloaded.host.querySelector('td')?.textContent).toBe('BeforeAfter')
    expect(reloaded.host.querySelectorAll('td br')).toHaveLength(1)
  })

  it('roundtrips a safe cell Shift-Enter through the legacy public editor seam', () => {
    const editor = new Editor({ content: seed, extensions: createMarkdownKit({ profile: 'markdown-safe' }).extensions })
    cleanups.push(() => editor.destroy())
    editor.commands.goToNextCell(); editor.commands.goToNextCell()
    editor.commands.setTextSelection(editor.state.selection.from)
    editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', shiftKey: true, bubbles: true }))
    editor.commands.insertContent('Inserted')
    expect(editor.view.dom.querySelectorAll('td br')).toHaveLength(1)
    const markdown = (editor.storage as unknown as { markdown: { getMarkdown(): string } }).markdown.getMarkdown()
    const reloaded = mount(markdown)
    expect(reloaded.host.querySelectorAll('tr')).toHaveLength(3)
    expect(reloaded.host.querySelectorAll('td br')).toHaveLength(1)
    expect(reloaded.host.querySelector('td')?.textContent).toBe('InsertedAlpha')
  })

  it('allows only bare table-cell br tags, leaving escaped tags, code and other HTML inert', () => {
    const { controller, host } = mount('| Type | Value |\n| --- | --- |\n| Break | A<br/>B |\n| Escaped | A\\<br>B |\n| Entity | A&lt;br&gt;B |\n| Code | `<br>` |\n| Attribute | A<br class=x>B |\n| HTML | <script>alert(1)</script> |\n\nOutside<br>table')
    expect(host.querySelectorAll('table br')).toHaveLength(1)
    expect(host.querySelector('script')).toBeNull()
    expect(host.querySelector('code')?.textContent).toBe('<br>')
    expect(host.textContent).toContain('A<br class=x>B')
    expect(host.textContent).toContain('Outside<br>table')
    const reloaded = mount(controller.getProjection().markdown)
    expect(reloaded.host.querySelectorAll('table br')).toHaveLength(1)
    expect(reloaded.host.textContent).toBe(host.textContent)
  })
})
