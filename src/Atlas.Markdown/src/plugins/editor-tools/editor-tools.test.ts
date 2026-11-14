// @vitest-environment jsdom
import { Editor, getSchema, type JSONContent } from '@tiptap/core'
import { DOMParser } from '@tiptap/pm/model'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownContentKit } from '../../core/kit'
import { hydrateMarkdownWithin } from '../../core'
import { createEditorToolsPlugin, findInEditor, replaceInEditor, editorSearchState, openEditorSearch } from './index'
import { createCodeBlockHighlighting, setCodeBlockLanguage } from './code'

const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()); document.body.replaceChildren() })
function mount(content: string | JSONContent, editable = true) {
  const instance = createEditorToolsPlugin().create({ profile: 'rich', signal: new AbortController().signal })
  const element = document.createElement('div'); document.body.append(element)
  const extensions = createMarkdownContentKit({ profile: 'rich', extraExtensions: [...instance.extensions!] }).extensions
  if (typeof content === 'string') {
    const fragment = document.createElement('div'); fragment.innerHTML = content
    content = DOMParser.fromSchema(getSchema(extensions)).parse(fragment).toJSON()
  }
  const editor = new Editor({ element, editable, extensions, content })
  editors.push(editor); return editor
}
describe('optional upstream editor tools', () => {
  it('leaves Enter and submit to composition without navigating search matches', async () => {
    const editor = mount('<p>cat cat</p>'); editor.view.focus(); await openEditorSearch(editor)
    const panel = document.querySelector<HTMLElement>('.md-search-panel')!
    const input = panel.querySelector<HTMLInputElement>('input[type=search]')!
    input.value = 'cat'; input.dispatchEvent(new Event('input', { bubbles: true }))
    await vi.waitFor(() => expect(editorSearchState(editor).results).toHaveLength(2))
    const before = editorSearchState(editor).currentIndex
    input.dispatchEvent(new CompositionEvent('compositionstart', { bubbles: true }))
    const enter = new KeyboardEvent('keydown', { key: 'Enter', isComposing: true, bubbles: true, cancelable: true })
    input.dispatchEvent(enter); panel.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    expect(enter.defaultPrevented).toBe(false); expect(editorSearchState(editor).currentIndex).toBe(before)
    input.dispatchEvent(new CompositionEvent('compositionend', { bubbles: true }))
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
    expect(editorSearchState(editor).currentIndex).not.toBe(before)
  })
  it('Nahradit vše says how many it replaced and gives the caret back to the text, so Ctrl+Z works at once', async () => {
    const editor = mount('<p>cat cat cat</p>'); editor.view.focus(); await openEditorSearch(editor)
    const panel = document.querySelector<HTMLElement>('.md-search-panel')!
    const input = panel.querySelector<HTMLInputElement>('input[type=search]')!
    input.value = 'cat'; input.dispatchEvent(new Event('input', { bubbles: true }))
    await vi.waitFor(() => expect(editorSearchState(editor).results).toHaveLength(3))
    panel.querySelector<HTMLInputElement>('input[type=text]')!.value = 'dog'
    panel.querySelector<HTMLInputElement>('input[type=text]')!.dispatchEvent(new Event('input', { bubbles: true }))
    const replaceAll = [...panel.querySelectorAll<HTMLButtonElement>('button')].find(button => button.textContent === 'Nahradit vše')!
    replaceAll.focus(); replaceAll.click()
    await vi.waitFor(() => expect(panel.querySelector('[role=status]')!.textContent).toBe('Nahrazeny 3 výskyty'))
    expect(editor.getText()).toBe('dog dog dog')
    expect(document.activeElement).toBe(editor.view.dom)
  })
  it('searches Czech text across marks and leaves readonly content untouched', () => {
    const editor = mount('<p>Pří<b>liš</b> žluťoučký. Příliš.</p>', false)
    const original = editor.getJSON()
    findInEditor(editor, 'Příliš')
    expect(editorSearchState(editor).results).toHaveLength(2)
    expect(replaceInEditor(editor, 'málo', true)).toBe(false)
    expect(editor.getJSON()).toEqual(original)
  })
  it('supports Unicode whole words and case sensitivity, and empty queries', () => {
    const editor = mount('<p>kůň kůňský KŮŇ</p>')
    findInEditor(editor, 'kůň', { wholeWord: true })
    expect(editorSearchState(editor).results).toHaveLength(2)
    findInEditor(editor, 'kůň', { wholeWord: true, caseSensitive: true })
    expect(editorSearchState(editor).results).toHaveLength(1)
    findInEditor(editor, '')
    expect(editorSearchState(editor).results).toHaveLength(0)
  })
  it('searches a long document and updates the affected textblock after edits', () => {
    const editor = mount({ type: 'doc', content: Array.from({ length: 1000 }, (_, index) => ({
      type: 'paragraph', content: [{ type: 'text', text: `${index}: Příliš žluťoučký kůň úpěl ďábelské ódy. ` .repeat(3) }],
    })) })
    findInEditor(editor, 'kůň', { wholeWord: true })
    expect(editorSearchState(editor).results).toHaveLength(3000)
    editor.commands.insertContentAt(1, 'kůň ')
    expect(editorSearchState(editor).results).toHaveLength(3001)
  })
  it('isolates consecutive replacements from each other and neighboring typing', () => {
    const editor = mount('<p>cat cat</p>')
    editor.commands.insertContentAt(8, '!')
    findInEditor(editor, 'cat')
    expect(replaceInEditor(editor, 'dog', true)).toBe(true)
    findInEditor(editor, 'dog')
    expect(replaceInEditor(editor, 'fox', true)).toBe(true)
    editor.commands.undo(); expect(editor.getText()).toBe('dog dog!')
    editor.commands.undo(); expect(editor.getText()).toBe('cat cat!')
    editor.commands.undo(); expect(editor.getText()).toBe('cat cat')
  })
  it('updates search after document edits without searching hidden attributes', () => {
    const editor = mount('<p>word</p><p>word</p>')
    findInEditor(editor, 'word')
    expect(editorSearchState(editor).results).toHaveLength(2)
    editor.commands.insertContentAt(1, 'word ')
    expect(editorSearchState(editor).results).toHaveLength(3)
  })
  it('uses safe RE2 regex and ignores empty matches, invalid expressions and hidden link attributes', () => {
    const editor = mount('<p><a href="https://example.test/secret">visible</a> 123 abc</p>')
    expect(findInEditor(editor, '\\d+', { useRegex: true })).toBe(true)
    expect(editorSearchState(editor).results).toHaveLength(1)
    findInEditor(editor, '^|$', { useRegex: true })
    expect(editorSearchState(editor).results).toHaveLength(0)
    expect(findInEditor(editor, '(?=abc)', { useRegex: true })).toBe(false)
    expect(findInEditor(editor, '[', { useRegex: true })).toBe(false)
    expect(editorSearchState(editor).results).toHaveLength(0)
    findInEditor(editor, 'secret')
    expect(editorSearchState(editor).results).toHaveLength(0)
  })
  it('searches table textblocks without crossing cells or rewriting invisible data', () => {
    const editor = mount('<table><tr><th>Ahoj</th><th>světe</th></tr><tr><td>Ahoj</td><td>světe</td></tr></table>')
    findInEditor(editor, 'Ahoj')
    expect(editorSearchState(editor).results).toHaveLength(2)
    findInEditor(editor, 'Ahojsvěte')
    expect(editorSearchState(editor).results).toHaveLength(0)
  })
  it('guards raw upstream replace commands in readonly too', () => {
    const editor = mount('<p>cat</p>', false)
    findInEditor(editor, 'cat'); editor.commands.setReplaceTerm('dog')
    expect(editor.commands.replace()).toBe(false)
    expect(editor.commands.replaceAll()).toBe(false)
    expect(editor.getText()).toBe('cat')
  })
  it('isolates the raw upstream replacement commands exposed to headless hosts', () => {
    const editor = mount('<p>cat cat</p>')
    findInEditor(editor, 'cat'); editor.commands.setReplaceTerm('dog'); editor.commands.replaceAll()
    findInEditor(editor, 'dog'); editor.commands.setReplaceTerm('fox'); editor.commands.replaceAll()
    editor.commands.undo(); expect(editor.getText()).toBe('dog dog')
    editor.commands.undo(); expect(editor.getText()).toBe('cat cat')
  })
  it('keeps search open when navigating away from a node selection', async () => {
    const editor = mount('<hr><p>cat</p>')
    editor.commands.setNodeSelection(0)
    await openEditorSearch(editor)
    editor.commands.setTextSelection(2)
    await new Promise(resolve => setTimeout(resolve, 0))
    expect(document.querySelector('[aria-label="Hledat a nahradit"]')).not.toBeNull()
  })
  it('cancels lazy panel opening when the owning view is detached', async () => {
    const editor = mount('<p>cat</p>')
    const opening = openEditorSearch(editor)
    editor.unmount()
    await expect(opening).resolves.toBeUndefined()
    expect(document.querySelector('[aria-label="Hledat a nahradit"]')).toBeNull()
  })
  it('decorates invisible characters only while they are shown (COL2-5)', async () => {
    const { SpaceCharacter } = await import('@tiptap/extension-invisible-characters')
    const decorate = vi.spyOn(SpaceCharacter.prototype, 'createDecoration')
    const editor = mount('<p>A B</p>')
    // A remote update replaces the whole document: hidden, nothing walks it.
    editor.commands.setContent('<p>C D E</p>')
    editor.commands.insertContent(' F')
    expect(decorate).not.toHaveBeenCalled()
    expect(editor.storage.invisibleCharacters.visibility()).toBe(false)
    editor.commands.toggleInvisibleCharacters()
    expect(editor.storage.invisibleCharacters.visibility()).toBe(true)
    expect(editor.view.dom.querySelectorAll('.tiptap-invisible-character--space')).toHaveLength(3)
    editor.commands.insertContent(' G')
    expect(editor.view.dom.querySelectorAll('.tiptap-invisible-character--space')).toHaveLength(4)
    editor.commands.toggleInvisibleCharacters()
    expect(editor.view.dom.querySelector('.tiptap-invisible-character')).toBeNull()
    decorate.mockClear()
    editor.commands.insertContent(' H')
    expect(decorate).not.toHaveBeenCalled()
    decorate.mockRestore()
  })
  it('renders non-breaking spaces as view decorations, without document or undo entries', () => {
    const editor = mount('<p>A\u00a0B C</p>')
    const original = editor.getJSON()
    expect(editor.view.dom.querySelector('.tiptap-invisible-character')).toBeNull()
    editor.commands.showInvisibleCharacters()
    expect(editor.view.dom.querySelector('.tiptap-invisible-character--nbsp')).not.toBeNull()
    expect(editor.getJSON()).toEqual(original)
    expect(editor.commands.undo()).toBe(false)
    editor.commands.hideInvisibleCharacters()
    expect(editor.getJSON()).toEqual(original)
  })
  it('replaces exactly one codeBlock schema and preserves fenced Markdown language', async () => {
    const codeBlock = await createCodeBlockHighlighting()
    const editor = new Editor({ extensions: createMarkdownContentKit({ codeBlock }).extensions,
      content: '```typescript\nconst x: number = 1\n```' })
    editors.push(editor)
    expect(editor.extensionManager.extensions.filter(extension => extension.name === 'codeBlock')).toHaveLength(1)
    expect(editor.state.doc.firstChild?.attrs.language).toBe('typescript')
    expect(editor.state.doc.firstChild?.textContent).toBe('const x: number = 1')
    await vi.waitFor(() => expect(editor.view.dom.querySelector('.hljs-keyword')).not.toBeNull())
  })
  it('highlights again only the code block a change touches and keeps the others in place (UX EP2-3)', async () => {
    const editor = new Editor({ extensions: createMarkdownContentKit({ codeBlock: await createCodeBlockHighlighting() }).extensions,
      content: 'Úvod\n\n```typescript\nconst a = 1\n```\n\n```typescript\nlet b = 2\n```' })
    editors.push(editor)
    const keywords = () => [...editor.view.dom.querySelectorAll('.hljs-keyword')].map(element => element.textContent)
    await vi.waitFor(() => expect(keywords()).toEqual(['const', 'let']))
    // Text before both blocks: their spans move with them.
    editor.commands.insertContentAt(1, 'Delší ')
    expect(keywords()).toEqual(['const', 'let'])
    // Typing in the second block highlights what it now holds.
    const second = editor.state.doc.child(2)
    let at = 0
    editor.state.doc.forEach((node, offset) => { if (node === second) at = offset })
    editor.commands.insertContentAt(at + 1 + second.content.size, ' return')
    expect(keywords()).toEqual(['const', 'let', 'return'])
    // A language change re-highlights its block only.
    expect(setCodeBlockLanguage(editor, at, 'plaintext')).toBe(true)
    expect(keywords()).toEqual(['const'])
  })
  it('keeps unknown languages readable and highlighting out of undo', async () => {
    const editor = new Editor({ extensions: createMarkdownContentKit({ codeBlock: await createCodeBlockHighlighting() }).extensions,
      content: '```unknown-language\nsome code\n```' })
    editors.push(editor)
    expect(editor.state.doc.firstChild?.attrs.language).toBe('unknown-language')
    expect(editor.view.dom.textContent).toContain('some code')
    expect(editor.commands.undo()).toBe(false)
  })
  it('isolates language changes, leaves code text intact, and bounds long-code highlighting', async () => {
    const editor = new Editor({ extensions: createMarkdownContentKit({ codeBlock: await createCodeBlockHighlighting({ maxHighlightLength: 5 }) }).extensions,
      content: '```javascript\nconst value = 123\n```' })
    editors.push(editor)
    expect(editor.view.dom.querySelector('.hljs-keyword')).toBeNull()
    expect(setCodeBlockLanguage(editor, 0, 'typescript')).toBe(true)
    expect(setCodeBlockLanguage(editor, 0, 'json')).toBe(true)
    editor.commands.undo(); expect(editor.state.doc.firstChild?.attrs.language).toBe('typescript')
    editor.commands.undo(); expect(editor.state.doc.firstChild?.attrs.language).toBe('javascript')
    expect(editor.state.doc.firstChild?.textContent).toBe('const value = 123')
    editor.setEditable(false); expect(setCodeBlockLanguage(editor, 0, 'json')).toBe(false)
  })
  it('keeps a headerless search panel usable in readonly, follows permissions and cleans up on Escape', async () => {
    const editor = mount('<p>cat cat</p>')
    editor.view.focus()
    await openEditorSearch(editor)
    const panel = document.querySelector<HTMLElement>('[role=dialog][aria-label="Hledat a nahradit"]')!
    expect(panel).not.toBeNull()
    expect(panel.querySelector('h1,h2,h3,.md-context-dismiss')).toBeNull()
    const input = panel.querySelector<HTMLInputElement>('input[type=search]')!
    input.value = 'cat'; input.dispatchEvent(new Event('input', { bubbles: true }))
    await vi.waitFor(() => expect(panel.textContent).toContain('1 / 2'))
    editor.setEditable(false)
    await vi.waitFor(() => expect(panel.querySelector('input[type=text]')).toBeNull())
    expect(panel.isConnected).toBe(true)
    // P3: the first Esc in the search clears the term and keeps the panel, the second one closes it.
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect(panel.isConnected).toBe(true)
    await vi.waitFor(() => expect(input.value).toBe(''))
    expect(editorSearchState(editor).results).toHaveLength(0)
    expect(input.hasAttribute('data-md-own-escape')).toBe(false)
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect(panel.isConnected).toBe(false)
    await openEditorSearch(editor)
    expect(document.querySelector('[role=dialog]')).not.toBeNull()
    editor.destroy()
    expect(document.querySelector('[role=dialog]')).toBeNull()
  })
  it('scopes Ctrl+F to the editor and keeps two editor search states independent', async () => {
    const first = mount('<p>one one</p>'); const second = mount('<p>two two</p>')
    findInEditor(first, 'one'); findInEditor(second, 'two')
    expect(editorSearchState(first).searchTerm).toBe('one')
    const outside = new KeyboardEvent('keydown', { key: 'f', ctrlKey: true, bubbles: true, cancelable: true })
    document.body.dispatchEvent(outside); expect(outside.defaultPrevented).toBe(false)
    const inside = new KeyboardEvent('keydown', { key: 'f', ctrlKey: true, bubbles: true, cancelable: true })
    first.view.dom.dispatchEvent(inside); expect(inside.defaultPrevented).toBe(true)
    await vi.waitFor(() => expect(document.querySelector('[role=dialog]')).not.toBeNull())
    expect(editorSearchState(second).searchTerm).toBe('two')
  })
  it('shows code controls only while that block owns selection and removes the portal on destroy', async () => {
    const element = document.createElement('div'); document.body.append(element)
    const editor = new Editor({ element, extensions: createMarkdownContentKit({ codeBlock: await createCodeBlockHighlighting() }).extensions,
      content: 'Before\n\n```javascript\nlet x = 1\n```' })
    editors.push(editor)
    editor.view.focus(); await Promise.resolve()
    const toolbar = document.querySelector<HTMLElement>('[role=toolbar][aria-label="Kód"]')!
    expect(toolbar.hidden).toBe(true)
    editor.commands.setTextSelection(10)
    await vi.waitFor(() => expect(toolbar.hidden).toBe(false))
    toolbar.querySelector<HTMLButtonElement>('[aria-label="Jazyk kódu"]')!.click()
    const menu = document.querySelector<HTMLElement>('[role=menu][aria-label="Jazyk kódu"]')!
    expect(menu).not.toBeNull()
    expect(menu.querySelector('h3')).toBeNull()
    expect(editor.getHTML()).not.toContain('Jazyk kódu')
    editor.destroy(); expect(toolbar.isConnected).toBe(false); expect(menu.isConnected).toBe(false)
  })
  it('opens the language of a selected code block with Enter or F2, its own edit (P1)', async () => {
    const element = document.createElement('div'); document.body.append(element)
    const editor = new Editor({ element, extensions: createMarkdownContentKit({ codeBlock: await createCodeBlockHighlighting() }).extensions,
      content: 'Before\n\n```javascript\nlet x = 1\n```' })
    editors.push(editor)
    await vi.waitFor(() => expect(document.querySelector('[role=toolbar][aria-label="Kód"]')).not.toBeNull())
    const menu = () => document.querySelector<HTMLElement>('[role=menu][aria-label="Jazyk kódu"]')
    // The first transaction adds the trailing paragraph after the code: count the blocks after it.
    editor.view.focus(); editor.commands.setNodeSelection(8)
    const blocks = editor.state.doc.childCount
    for (const key of ['Enter', 'F2']) {
      editor.view.focus(); editor.commands.setNodeSelection(8)
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true })
      editor.view.dom.dispatchEvent(event)
      expect(event.defaultPrevented, key).toBe(true)
      expect(menu(), key).not.toBeNull()
      document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
      expect(menu(), key).toBeNull()
    }
    // Enter took no paragraph next to the block.
    expect(editor.state.doc.childCount).toBe(blocks)
  })
})
// Last in the file: the document keeps the stubbed observer of its proximity registry.
describe('code blocks away from the viewport', () => {
  it('stay their plain code without a toolbar until they come near, then highlight, also after a language change (plan 3.6-G)', async () => {
    vi.stubGlobal('IntersectionObserver', class { observe() {} unobserve() {} })
    const element = document.body.appendChild(document.createElement('div'))
    const editor = new Editor({ element, extensions: createMarkdownContentKit({ codeBlock: await createCodeBlockHighlighting() }).extensions,
      content: '```javascript\nlet x = 1\n```' })
    editors.push(editor)
    const keyword = () => editor.view.dom.querySelector('.hljs-keyword')?.textContent
    await new Promise(resolve => setTimeout(resolve))
    expect(keyword()).toBeUndefined()
    expect(editor.view.dom.textContent).toContain('let x = 1')
    expect(document.querySelector('[role=toolbar][aria-label="Kód"]')).toBeNull()
    hydrateMarkdownWithin(editor.view.dom)
    await vi.waitFor(() => expect(keyword()).toBe('let'))
    expect(document.querySelector('[role=toolbar][aria-label="Kód"]')).not.toBeNull()
    // The language replaces the node's markup: its view asks again.
    expect(setCodeBlockLanguage(editor, 0, 'typescript')).toBe(true)
    await vi.waitFor(() => expect(editor.view.dom.querySelector('code')?.className).toBe('language-typescript'))
    await vi.waitFor(() => expect(keyword()).toBe('let'))
    vi.unstubAllGlobals()
  })
})
