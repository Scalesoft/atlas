// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import type { Editor } from '@tiptap/core'
import { NodeSelection } from '@tiptap/pm/state'
import { createMarkdownChip, createMarkdownEditorController, markdownChipSpec, type MarkdownEditorController, type MarkdownRichValue } from '../../core'
import { editorForController } from '../../core/internal'
import { createPagesPlugin } from '../../plugins/pages'
import { createYouTrackPlugin } from '../../plugins/youtrack'

Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }

describe('the inline chip', () => {
  it('builds the slots, the state and what a screen reader says', () => {
    const avatar = document.createElement('span')
    const { element, update } = createMarkdownChip(document, { variant: 'mention', label: 'Jana Nováková', media: avatar, state: 'unresolved',
      detail: [{ text: 'nejspíš Modelsoft', className: 'likely' }, { text: ' · nebo Example' }], ariaLabel: 'Jana Nováková, nerozhodnutý kontakt', editable: true })
    expect(element.className).toBe('md-chip md-chip--mention')
    expect([...element.children].map(child => child.className)).toEqual(['md-chip-media', 'md-chip-label', 'md-chip-detail likely', 'md-chip-detail', 'md-chip-mark'])
    expect(element.dataset.chipState).toBe('unresolved')
    expect(element.getAttribute('aria-label')).toBe('Jana Nováková, nerozhodnutý kontakt')
    expect(element.title).toBe('Ctrl+Shift+Mezerník převede na text · Ctrl+Mezerník nabídne, co s ním jde dělat')
    update({ state: undefined, detail: undefined, ariaLabel: undefined, editable: false })
    expect(element.dataset.chipState).toBeUndefined()
    expect(element.querySelector('.md-chip-detail, .md-chip-mark')).toBeNull()
    expect(element.getAttribute('aria-label')).toBe('Jana Nováková')
    expect(element.hasAttribute('title')).toBe(false)
  })
  it('renders the same markup as a DOM spec, for HTML and readers', () => {
    expect(markdownChipSpec({ tag: 'a', href: '/x', label: [{ text: 'PX2-1: ', className: 'key' }, { text: 'VPN' }], detail: ' · Open', className: 'md-youtrack-issue' }, { 'data-x': '1' }))
      .toEqual(['a', { 'data-x': '1', class: 'md-chip md-chip--tag md-youtrack-issue', href: '/x' }, ['span', { class: 'md-chip-label key' }, 'PX2-1: '], ['span', { class: 'md-chip-label' }, 'VPN'], ['span', { class: 'md-chip-detail' }, ' · Open']])
  })
})

// Every chip of the editor package that is a chip: the same classes and states; the back-to-text keys are in chipText.test.ts.
describe('chips are one component', () => {
  const controllers: MarkdownEditorController[] = []
  afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })
  const issue = { type: 'youtrackIssue', attrs: { id: '2-1', key: 'PX2-1', title: 'Přístup VPN', url: 'https://youtrack.example/issue/PX2-1', state: 'Open' } }
  const page = { type: 'pageReference', attrs: { id: 'wiki-1', title: 'Provozní příručka', url: '/wiki/1' } }
  function mount(): Editor {
    const plugins = [createYouTrackPlugin({ client: { search: async () => [] } }), createPagesPlugin()]
    const controller = createMarkdownEditorController({ profile: 'rich', plugins, source: { kind: 'local', value: { format: 'atlas-rich', version: 1,
      plugins: plugins.map(plugin => ({ id: plugin.id, version: plugin.schemaVersion ?? 1 })), document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Úkol ' }, issue, { type: 'text', text: ' a ' }, page] }] } as MarkdownRichValue['document'] } } })
    controllers.push(controller)
    const host = document.createElement('div'); document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Editor' })
    return editorForController(controller)!
  }
  it('an issue and a page are tags with the chip keys in their tooltip, and a selected one has the selected state', () => {
    const editor = mount()
    const chips = [...editor.view.dom.querySelectorAll<HTMLElement>('.md-chip')]
    expect(chips.map(chip => [chip.className, chip.tagName])).toEqual([['md-chip md-chip--tag md-youtrack-issue', 'A'], ['md-chip md-chip--tag md-page-reference', 'A']])
    expect(chips.every(chip => chip.title.includes('Ctrl+Shift+Mezerník převede na text'))).toBe(true)
    expect(chips[0]!.querySelector('.md-chip-detail')?.textContent).toBe(' · Open')
    editor.view.dispatch(editor.state.tr.setSelection(NodeSelection.create(editor.state.doc, 6)))
    expect(editor.view.dom.querySelector('.md-chip.ProseMirror-selectednode')?.classList.contains('md-youtrack-issue')).toBe(true)
  })
})
