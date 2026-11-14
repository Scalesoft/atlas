// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import { Editor, type JSONContent } from '@tiptap/core'
import { createMarkdownKit } from '../../createMarkdownKit'
import { blockActions, turnInto } from './turnInto'
import { openMarkdownBlockMenu, openMarkdownIntentions, type MarkdownIntention } from './intentions'

const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()); document.body.replaceChildren() })
function mount(content: string) {
  const element = document.createElement('div')
  document.body.appendChild(element)
  const editor = new Editor({ element, content, extensions: createMarkdownKit({ profile: 'rich' }).extensions })
  editors.push(editor)
  return editor
}
const rows = (editor: Editor) => turnInto({ editor, block: true, from: 1, to: 1, empty: true, text: '', signal: new AbortController().signal }) as { id: string; label: string; icon: string; checked?: boolean; run(): void }[]
const row = (editor: Editor, id: string) => rows(editor).find(item => item.id === `core.turn-into.${id}`)!

describe('Změnit na…', () => {
  it('offers every kind the top toolbar had, the current one checked and doing nothing, each with its own icon (§5.4)', () => {
    const editor = mount('Text')
    editor.commands.setTextSelection(2)
    const ids = rows(editor).map(item => item.id.split('.').pop())
    expect(ids).toEqual(['paragraph', 'h1', 'h2', 'h3', 'h4', 'bullet', 'ordered', 'task', 'timed', 'quote', 'callout', 'code'])
    expect(rows(editor).filter(item => item.checked).map(item => item.id)).toEqual(['core.turn-into.paragraph'])
    expect(new Set(rows(editor).map(item => item.icon)).size).toBe(ids.length)
    expect(row(editor, 'task').label).toBe('Změnit na: Úkoly')
    const before = editor.getJSON()
    row(editor, 'paragraph').run()
    expect(editor.getJSON()).toEqual(before)
    expect(editor.can().undo()).toBe(false)
  })
  it('turns a paragraph into a heading, a callout and back in one undo step each', () => {
    const editor = mount('Text')
    editor.commands.setTextSelection(2)
    row(editor, 'h2').run()
    expect(editor.getJSON().content![0]).toMatchObject({ type: 'heading', attrs: { level: 2 } })
    editor.commands.undo()
    expect(editor.getJSON().content![0]!.type).toBe('paragraph')
    row(editor, 'callout').run()
    expect(editor.getJSON().content![0]!.type).toBe('callout')
    expect(row(editor, 'callout').checked).toBe(true)
    row(editor, 'paragraph').run()
    expect(editor.getJSON().content![0]!.type).toBe('paragraph')
  })
  it('turns a list item into another list kind and a timed list', () => {
    const editor = mount('- one')
    editor.commands.setTextSelection(4)
    row(editor, 'ordered').run()
    expect(editor.getJSON().content![0]!.type).toBe('orderedList')
    row(editor, 'timed').run()
    expect(editor.getJSON().content![0]!.type).toBe('timedList')
  })
  it('stays quiet over a plain caret, in tables and in read-only editors', () => {
    const plain = mount('Text')
    plain.commands.setTextSelection(2)
    expect(turnInto({ editor: plain, from: 2, to: 2, empty: true, text: '', signal: new AbortController().signal })).toEqual([])
    const editor = mount('| A | B |\n| --- | --- |\n| 1 | 2 |')
    editor.commands.setTextSelection(6)
    expect(rows(editor)).toEqual([])
    const reader = mount('Text')
    reader.setEditable(false)
    expect(rows(reader)).toEqual([])
  })
})

/* Every convertible block into every other kind (J4.9): the kind changes, the text stays, one Zpět restores it. */
const text = (value: string) => [{ type: 'text', text: value }]
const p = (value: string) => ({ type: 'paragraph', content: text(value) })
const items = (item: string, attrs = {}) => ['alfa', 'beta'].map(value => ({ type: item, attrs, content: [p(value)] }))
const sources: Record<string, JSONContent> = {
  paragraph: p('alfa'),
  h2: { type: 'heading', attrs: { level: 2 }, content: text('alfa') },
  bullet: { type: 'bulletList', content: items('listItem') },
  ordered: { type: 'orderedList', content: items('listItem') },
  task: { type: 'taskList', content: items('taskItem', { checked: false }) },
  timed: { type: 'timedList', content: items('timedItem') },
  quote: { type: 'blockquote', content: [p('alfa'), p('beta')] },
  callout: { type: 'callout', attrs: { variant: 'tip' }, content: [p('alfa'), p('beta')] },
  code: { type: 'codeBlock', content: text('alfa') },
}
const kinds: Record<string, string> = { paragraph: 'paragraph', bulletList: 'bullet', orderedList: 'ordered', taskList: 'task', timedList: 'timed', blockquote: 'quote', callout: 'callout', codeBlock: 'code' }
const kindOf = (node: JSONContent) => node.type === 'heading' ? `h${node.attrs!.level}` : kinds[node.type!]
const targetIds = ['paragraph', 'h1', 'h2', 'h3', 'h4', 'bullet', 'ordered', 'task', 'timed', 'quote', 'callout', 'code']
const matrix = Object.keys(sources).flatMap(source => targetIds.filter(target => target !== source).map(target => [source, target] as const))

describe('Změnit na… for every source and target', () => {
  it.each(matrix)('%s → %s', (source, target) => {
    const editor = mount('')
    editor.commands.setContent({ type: 'doc', content: [p('Před'), sources[source]!, p('Po')] })
    const before = editor.getJSON()
    let at = 0
    editor.state.doc.descendants((node, pos) => { if (node.isText && node.text === 'alfa') at = pos + 1 })
    editor.commands.setTextSelection(at)
    row(editor, target).run()
    const after = editor.getJSON().content!
    expect(after[0]).toEqual(before.content![0])
    expect(kindOf(after[1]!)).toBe(target)
    expect(after.at(-1)).toEqual(before.content!.at(-1))
    const words = editor.state.doc.textBetween(0, editor.state.doc.content.size, ' ')
    for (const word of ['Před', 'alfa', ...source === 'paragraph' || source === 'h2' || source === 'code' ? [] : ['beta'], 'Po']) expect(words).toContain(word)
    editor.commands.undo()
    expect(editor.getJSON()).toEqual(before)
  })
  it('keeps a nested list when a list changes its kind', () => {
    const editor = mount('- one\n  - inner\n- two')
    editor.commands.setTextSelection(4)
    row(editor, 'ordered').run()
    const list = (editor.getJSON() as JSONContent).content![0]!
    expect(list.type).toBe('orderedList')
    expect(list.content![0]!.content![1]!.type).toBe('bulletList')
  })
  it('a timed list turned into anything else keeps its dates as leading text, one Zpět brings the list back', () => {
    for (const target of ['paragraph', 'bullet', 'callout']) {
      const editor = mount('')
      const stamped = { type: 'timedList', content: ['3. 11. 2026 17:19 — alfa', '2. 11. 2026 — beta'].map(value => ({ type: 'timedItem', content: [p(value)] })) }
      editor.commands.setContent({ type: 'doc', content: [stamped] })
      const before = editor.getJSON()
      editor.commands.setTextSelection(25)
      row(editor, target).run()
      expect(editor.state.doc.textContent).toBe('3. 11. 2026 17:19 — alfa2. 11. 2026 — beta')
      editor.commands.undo()
      expect(editor.getJSON()).toEqual(before)
    }
  })
  it('offers nothing for a block that does not turn (a rule, a table)', () => {
    const editor = mount('Text\n\n---')
    editor.commands.setNodeSelection(6)
    expect(rows(editor)).toEqual([])
  })
})

describe('the block’s own rows', () => {
  it('duplicates, moves and deletes the block with the caret, the focus back in the text', () => {
    const editor = mount('one\n\ntwo')
    editor.commands.setTextSelection(2)
    const action = (id: string) => (blockActions({ editor, block: true, from: 2, to: 2, empty: true, text: '', signal: new AbortController().signal }) as readonly MarkdownIntention[]).find(item => item.id === id)!
    const texts = () => (editor.getJSON() as JSONContent).content!.map(node => node.content?.[0]?.text)
    action('core.block.duplicate').run()
    expect(texts()).toEqual(['one', 'one', 'two'])
    editor.commands.setTextSelection(2)
    action('core.block.down').run()
    expect(texts()).toEqual(['one', 'one', 'two'])
    editor.commands.setTextSelection(editor.state.doc.content.size - 2)
    action('core.block.up').run()
    expect(texts()).toEqual(['one', 'two', 'one'])
    action('core.delete').run()
    expect(texts()).toEqual(['one', 'one'])
    expect(document.activeElement).toBe(editor.view.dom)
    expect(blockActions({ editor, from: 2, to: 2, empty: true, text: '', signal: new AbortController().signal })).toEqual([])
  })
})

describe('the menu itself', () => {
  it('opens from the block by keyboard: typing filters, Enter runs the first row, one Zpět undoes it', () => {
    const editor = mount('> Quoted')
    expect(openMarkdownBlockMenu(editor, 0)).toBe(true)
    expect(document.activeElement).toBe(editor.view.dom)
    for (const key of 'odst') editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
    const titles = [...document.querySelectorAll('[data-testid=intentions] [role=option]')].map(option => option.textContent!.trim())
    expect(titles).toEqual(['Změnit na: Odstavec'])
    editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
    expect(editor.getJSON().content![0]).toMatchObject({ type: 'paragraph', content: [{ text: 'Quoted' }] })
    editor.commands.undo()
    expect(editor.getJSON().content![0]!.type).toBe('blockquote')
  })
  it('lists the rows when opened from the block, with the caret inside a word', () => {
    const editor = mount('# Head')
    editor.commands.setTextSelection(3)
    expect(openMarkdownIntentions(editor, { block: true })).toBe(true)
    const titles = [...document.querySelectorAll('[data-testid=intentions] [role=option]')].map(option => option.textContent!.trim())
    expect(titles).toContain('Změnit na: Odstavec')
    expect(titles).toContain('Změnit na: Časová odrážka')
    // The current kind is marked, not preselected: the first other row is.
    const current = document.querySelector('[data-testid=intentions] [aria-checked=true]')
    expect(current?.textContent?.trim()).toBe('Změnit na: Nadpis 1')
    expect(current?.querySelector('[data-icon=check]')).not.toBeNull()
    expect(document.querySelector('[data-testid=intentions] [aria-selected=true]')?.textContent?.trim()).toBe('Změnit na: Odstavec')
  })
  it('never preselects the current kind, also when it is the first row', () => {
    const editor = mount('Text')
    editor.commands.setTextSelection(2)
    openMarkdownIntentions(editor, { block: true })
    expect(document.querySelector('[data-testid=intentions] [role=option]')?.getAttribute('aria-checked')).toBe('true')
    expect(document.querySelector('[data-testid=intentions] [aria-selected=true]')?.textContent?.trim()).toBe('Změnit na: Nadpis 1')
  })
})
