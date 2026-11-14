// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import { Editor, type JSONContent } from '@tiptap/core'
import type { Node as PMNode } from '@tiptap/pm/model'
import { EditorState, TextSelection } from '@tiptap/pm/state'
import StarterKit from '@tiptap/starter-kit'
import Collaboration from '@tiptap/extension-collaboration'
import { yUndoPluginKey } from '@tiptap/y-tiptap'
import * as Y from 'yjs'
import { CollaborationHistoryBoundary } from '../../collab/historyBoundary'
import { ChipText } from '../../features/chips/chipText'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin } from '..'
import { editorForController } from '../internal'
import { createContactsPlugin } from '../../plugins/contacts'
import { personEntries } from '../../plugins/contacts/namesLoader'
import { createPagesPlugin } from '../../plugins/pages'
import { createYouTrackPlugin } from '../../plugins/youtrack'
import { MarkdownRecognition } from './runtime'
import { buildRecognitionIndex, recognizeTail, type MarkdownRecognitionEntry } from './recognize'
import { recognitionWindow, RECOGNITION_ENDS, typedTrigger } from './gate'
import type { MarkdownLexicon } from './lexicon'

/*
 * The golden corpus of the linking engine (tests/fixtures/linking/corpus.json), through the pure core and through a real editor: the
 * same typed text gives the same decisions, the same document, the same Backspace and the same Ctrl+Z over a Y.Doc. A case that differs
 * is named. The P17 budget of a keystroke (gate, window, match) is measured here too.
 */

Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }

interface TypingCase { name: string; lexicon: string; typed: string; hits: { text: string; decision: string }[]; after: string; backspace: string; undo?: string; block?: 'heading'; linked?: string }
const fs = await import(/* @vite-ignore */ `node:${'fs'}`) as { readFileSync(path: string, encoding: 'utf8'): string }
const { resolve } = await import(/* @vite-ignore */ `node:${'path'}`) as { resolve(...paths: string[]): string }
// Vitest runs in src/Atlas.Markdown; the corpus is the repository's, shared with the server port.
const corpusPath = resolve((globalThis as unknown as { process: { cwd(): string } }).process.cwd(), '../../tests/fixtures/linking/corpus.json')
const corpus = JSON.parse(fs.readFileSync(corpusPath, 'utf8')) as {
  lexicons: Record<string, MarkdownLexicon>; issues: Record<string, string>; typing: TypingCase[]
}

// Cold import of the engine, so the first real-editor test types into a landed engine as every later one does on a loaded host.
await import('./engine')

const pageEntries = (lexicon: MarkdownLexicon): MarkdownRecognitionEntry[] => lexicon.pages.map(page => ({ kind: 'page', id: page.id, name: page.title, form: 'full', auto: false, target: page }))
const noDoc = { descendants: () => undefined } as unknown as PMNode
const indexOf = (lexicon: MarkdownLexicon) => buildRecognitionIndex([...personEntries({}, lexicon, noDoc), ...pageEntries(lexicon)], new Set(['person']))

/** The pure core over the text as typed: its decision at every character that ends a word (what the gate lets through). */
function pureHits(test: TypingCase) {
  if (test.block === 'heading') return []
  const lexicon = corpus.lexicons[test.lexicon]!, index = indexOf(lexicon), hits: { text: string; decision: string }[] = []
  // Text inside a link is not prose: the editor reads it as U+FFFC.
  let text = '￼'.repeat(test.linked?.length ?? 0)
  for (const char of test.typed) {
    text += char
    if (!RECOGNITION_ENDS.test(char)) continue
    const hit = recognizeTail(recognitionWindow(text, text.length).text, index, lexicon)
    if (hit && hit.decision !== 'none') hits.push({ text: hit.text, decision: hit.decision })
  }
  return hits
}

const issue = (key: string) => ({ id: `id-${key}`, key, title: corpus.issues[key]!, url: `https://youtrack.example/issue/${key}` })
const plugins = (): MarkdownPlugin[] => [createContactsPlugin({}),
  createYouTrackPlugin({ client: { search: async () => [], resolve: async key => (corpus.issues[key] ? issue(key) : null) } }),
  createPagesPlugin({ source: { search: async () => [] } })]
/** A document as the corpus writes it: text, a chip as [kind:text]. */
const written = (doc: PMNode) => {
  const blocks: string[] = []
  doc.forEach(block => {
    let line = ''
    block.forEach(node => {
      const contact = (node.attrs.data as { contacts?: { name: string; unresolved?: boolean }[] } | null)?.contacts?.[0]
      line += node.isText ? node.text! : node.type.name === 'contactInline' ? `[${contact?.unresolved ? 'unresolved' : 'person'}:${contact?.name}]`
        : node.type.name === 'youtrackIssue' ? `[issue:${node.attrs.key}]` : node.type.name === 'pageReference' ? `[page:${node.attrs.title}]` : `[${node.type.name}]`
    })
    blocks.push(line)
  })
  // A trailing empty paragraph the editor keeps after a heading is no text.
  return blocks.join('\n').replace(/\n+$/u, '')
}
const settle = async () => { for (let i = 0; i < 5; i++) await new Promise(resolve => setTimeout(resolve, 5)) }
/** One key at a time, as the keyboard does (input rules included), each its own task. */
async function type(editor: Editor, text: string) {
  const view = editor.view
  for (const char of text) {
    const { from, to } = view.state.selection
    if (!view.someProp('handleTextInput', handle => handle(view, from, to, char, () => view.state.tr.insertText(char, from, to)))) view.dispatch(view.state.tr.insertText(char, from, to))
    await Promise.resolve()
  }
  await settle()
}
const start = (test: TypingCase): JSONContent[] => [test.block === 'heading' ? { type: 'heading', attrs: { level: 2 } }
  : { type: 'paragraph', content: test.linked ? [{ type: 'text', text: test.linked, marks: [{ type: 'link', attrs: { href: 'https://example.com' } }] }] : [] }]

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })
function mount(test: TypingCase) {
  const lexicon = corpus.lexicons[test.lexicon]!, list = plugins()
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: list, lexicon: { read: () => lexicon },
    source: { kind: 'local', value: { format: 'atlas-rich', version: 1, plugins: list.map(plugin => ({ id: plugin.id, version: plugin.schemaVersion ?? 1 })), document: { type: 'doc', content: start(test) } as never } } })
  controllers.push(controller)
  const host = document.createElement('div'); host.className = 'md-editor'; document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Editor' })
  const editor = editorForController(controller)! as Editor
  editor.view.focus(); editor.commands.setTextSelection(editor.state.doc.firstChild!.nodeSize - 1)
  return editor
}
/** Backspace as the keyboard: the editor's handlers first, else the character before the caret goes (jsdom has no native editing). */
function backspace(editor: Editor) {
  const unhandled = editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: 'Backspace', bubbles: true, cancelable: true }))
  if (unhandled) { const { from } = editor.state.selection; editor.view.dispatch(editor.state.tr.delete(from - 1, from)) }
}

describe('linking corpus: the pure core', () => {
  for (const test of corpus.typing) it(test.name, () => expect(pureHits(test)).toEqual(test.hits))
})

describe('linking corpus: a real editor', () => {
  for (const test of corpus.typing) it(test.name, async () => {
    const editor = mount(test)
    await settle()
    await type(editor, test.typed)
    expect(written(editor.state.doc), 'after typing').toBe(test.after)
    backspace(editor)
    expect(written(editor.state.doc), 'after Backspace').toBe(test.backspace)
  })
})

describe('linking corpus: Ctrl+Z over a Y.Doc undoes only the chip', () => {
  for (const test of corpus.typing.filter(test => test.undo)) it(test.name, async () => {
    const document = new Y.Doc(), lexicon = corpus.lexicons[test.lexicon]!
    const extensions = plugins().flatMap(plugin => plugin.create({ profile: 'rich', signal: new AbortController().signal }).extensions ?? [])
    const editor = new Editor({ element: globalThis.document.body.appendChild(globalThis.document.createElement('div')),
      extensions: [StarterKit.configure({ undoRedo: false }), Collaboration.configure({ document }), CollaborationHistoryBoundary, ChipText, MarkdownRecognition.configure({ lexicon: { read: () => lexicon } }), ...extensions] })
    try {
      yUndoPluginKey.getState(editor.state)!.undoManager.clear()
      editor.commands.focus('end')
      await settle()
      for (const char of test.typed) { editor.view.dispatch(editor.state.tr.insertText(char)); await Promise.resolve() }
      await settle()
      expect(written(editor.state.doc), 'after typing').toBe(test.after)
      editor.commands.undo()
      expect(written(editor.state.doc), 'after Ctrl+Z').toBe(test.undo)
    } finally { editor.destroy(); document.destroy() }
  })
})

// P17: the gate, the window and the match of one keystroke within 1 ms p95 in 5 000 paragraphs, against a lexicon of 1 000 people and
// 2 000 pages; the cost does not grow with the document (one textblock visited), so 20 paragraphs cost the same.
describe('linking engine: the keystroke budget (P17)', () => {
  const lexicon: MarkdownLexicon = { version: 'budget', truncated: false, youTrackProjects: ['PX2'],
    people: Array.from({ length: 1000 }, (_, i) => ({ key: `k${i}`, name: `Osoba${i} Příjmení${i % 97}`, aliases: [`Ali${i}`], uses: 0 })),
    pages: Array.from({ length: 2000 }, (_, i) => ({ id: `p${i}`, title: `Stránka číslo ${i}` })) }
  const index = indexOf(lexicon)
  function keystrokes(paragraphs: number) {
    const schema = new Editor({ extensions: [StarterKit] }).schema
    const doc = schema.node('doc', null, Array.from({ length: paragraphs }, (_, i) => schema.node('paragraph', null, schema.text(`Odstavec ${i}: volala Osoba${i} Příjmení${i % 97} kvůli Stránka číslo ${i} a PX2-${i + 1}.`))))
    let middle = 0
    doc.forEach((node, offset, i) => { if (i === Math.floor(paragraphs / 2)) middle = offset + node.nodeSize - 1 })
    let state = EditorState.create({ doc, selection: TextSelection.create(doc, middle) })
    const times: number[] = []
    for (const char of ' Osoba3 Příjmení3 a Ali5, Stránka číslo 7 PX2-12 '.repeat(4)) {
      const tr = state.tr.insertText(char)
      const next = state.apply(tr)
      const started = performance.now()
      const typed = typedTrigger([tr], state, next)
      if (typed) recognizeTail(typed.text, index, lexicon)
      times.push(performance.now() - started)
      state = next
    }
    return times.sort((a, b) => a - b)[Math.floor(times.length * 0.95)]!
  }
  it('stays within 1 ms p95 per keystroke in 5 000 paragraphs, as in 20', () => {
    keystrokes(20)
    const short = keystrokes(20), long = keystrokes(5000)
    expect(long, `p95 ${long.toFixed(3)} ms in 5 000 paragraphs`).toBeLessThan(1)
    expect(long, `p95 ${long.toFixed(3)} ms in 5 000 paragraphs vs ${short.toFixed(3)} ms in 20`).toBeLessThan(short * 3 + 0.2)
  })
})
