import type { Editor, JSONContent } from '@tiptap/core'
import { Fragment, type Node } from '@tiptap/pm/model'
import type { Transaction } from '@tiptap/pm/state'
import { Mapping } from '@tiptap/pm/transform'
import { markdownChangeMapping, parseMarkdownContent, serializeMarkdownContent } from '../core'
import { mapRange } from './text'
import type { MarkdownAssistRange } from './types'

// Only ordinary Markdown participates. Every other node/mark is an opaque local value.
const nodes = new Set(['doc', 'paragraph', 'heading', 'blockquote', 'bulletList', 'orderedList', 'listItem', 'codeBlock', 'hardBreak', 'horizontalRule', 'table', 'tableRow', 'tableCell', 'tableHeader', 'text'])
const marks = new Set(['bold', 'italic', 'strike', 'code'])
const tokens = /⟦c:[a-f0-9]{16}:[1-9][0-9]{0,5}⟧/g
// A heading or paragraph with a host's own attribute set (a requirement heading's state, a dependency line) is no ordinary Markdown:
// its text would come back without it. The level, the textAlign and a heading's identity (carried apart) are ordinary.
const ordinaryAttributes = new Set(['level', 'textAlign', 'headingId', 'aliases'])
const hostAttributes = (node: Node) => (node.type.name === 'heading' || node.type.name === 'paragraph')
  && Object.entries(node.attrs).some(([name, value]) => !ordinaryAttributes.has(name) && value !== (node.type.spec.attrs?.[name]?.default ?? null))
const forbidden = (node: Node) => /secret|conceal/i.test(node.type.name) || node.marks.some(mark => /secret|conceal/i.test(mark.type.name))

/** A valid, non-empty range without a secret or concealed part in or around it: the only kind a provider may get. */
function sendable(editor: Editor, range: MarkdownAssistRange): boolean {
  if (editor.isDestroyed || range.from < 0 || range.to > editor.state.doc.content.size || range.from >= range.to) return false
  let secret = false
  editor.state.doc.nodesBetween(range.from, range.to, node => { if (forbidden(node)) secret = true })
  for (const pos of [range.from, range.to]) {
    const resolved = editor.state.doc.resolve(pos)
    for (let depth = 0; depth <= resolved.depth; depth++) if (forbidden(resolved.node(depth))) secret = true
  }
  return !secret
}

/** A document of a slice's content; inline content gets its paragraph. */
function documentOf(editor: Editor, content: Fragment): Node {
  const source: Node[] = []; content.forEach(node => source.push(node))
  return editor.schema.topNodeType.create(null, source[0]?.isInline ? [editor.schema.nodes.paragraph!.create(null, source)] : source)
}

/**
 * The Markdown of a range for an AI action that only reads (Zeptat se, Vysvětlit, Shrnout; P11): every element in its own dialect
 * (a chart's data, a formula, a diagram's source), so it can be explained. Null for a secret or concealed part, as in the projection.
 */
export function readMarkdownAssistText(editor: Editor, range: MarkdownAssistRange): string | null {
  if (!sendable(editor, range)) return null
  try { return serializeMarkdownContent(editor, documentOf(editor, editor.state.doc.slice(range.from, range.to).content)).trim() || null } catch { return null }
}

/** A local, disposable snapshot. Only `text` may be sent to a provider; nodes/attributes never leave this object. */
export interface MarkdownAssistProjection {
  readonly text: string
  readonly before: JSONContent
  /** Null after a content/attribute change or disposal. Mapped through edits outside the selection. */
  readonly range: MarkdownAssistRange | null
  restore(text: string): JSONContent
  dispose(): void
}

/** Snapshot a safe selection before awaiting a model. Returns null for secrets, invalid ranges or token collisions. */
export function projectMarkdownAssistSelection(editor: Editor, selection: MarkdownAssistRange = editor.state.selection): MarkdownAssistProjection | null {
  if (!sendable(editor, selection)) return null
  const snapshot = editor.state.doc.slice(selection.from, selection.to)
  const originals = new Map<string, Node>()
  const nonce = Array.from(crypto.getRandomValues(new Uint8Array(8)), byte => byte.toString(16).padStart(2, '0')).join('')
  const mask = (node: Node): Node => {
    if (!nodes.has(node.type.name) || hostAttributes(node) || node.marks.some(mark => !marks.has(mark.type.name))) {
      const token = `⟦c:${nonce}:${originals.size + 1}⟧`
      originals.set(token, node)
      return node.isInline ? editor.schema.text(token) : editor.schema.nodes.paragraph!.create(null, editor.schema.text(token))
    }
    if (node.isText && /[⟦⟧]/u.test(node.text!)) throw new Error('Reserved assist token delimiter.')
    const children: Node[] = []; node.forEach(child => children.push(mask(child)))
    return node.isLeaf ? node : node.copy(Fragment.from(children))
  }
  let text: string
  const beforeDoc = documentOf(editor, snapshot.content)
  try { text = serializeMarkdownContent(editor, mask(beforeDoc)) } catch { return null }
  let range: MarkdownAssistRange | null = { from: selection.from, to: selection.to }
  const track = ({ transaction, appendedTransactions }: { transaction: Transaction; appendedTransactions: Transaction[] }) => {
    if (!range) return
    const mapping = new Mapping(); [transaction, ...appendedTransactions].forEach(item => mapping.appendMapping(markdownChangeMapping(item)))
    const mapped = mapRange(mapping, range)
    range = mapped && editor.state.doc.slice(mapped.from, mapped.to).eq(snapshot) ? mapped : null
  }
  editor.on('transaction', track)
  const dispose = () => { range = null; editor.off('transaction', track); editor.off('destroy', dispose) }
  editor.on('destroy', dispose)
  return {
    text, before: beforeDoc.toJSON(), get range() { return range }, dispose,
    restore(result) {
      if (!range || editor.isDestroyed) throw new Error('The selection has changed.')
      if (/(^|\s):{1,3}[a-z][\w-]*(\{|\[|\s|$)/i.test(result) || /(password|passwd|heslo|api[_ -]?key|token|secret)\s*[:=]\s*\S+/i.test(result)) throw new Error('Host directives or secrets are not allowed.')
      const returned = result.match(tokens) ?? []
      if (JSON.stringify(returned) !== JSON.stringify([...originals.keys()]) || /[⟦⟧]/u.test(result.replace(tokens, ''))) throw new Error('Opaque tokens changed.')
      const restore = (node: Node): Node[] => {
        if (!nodes.has(node.type.name) || node.marks.some(mark => !marks.has(mark.type.name))) throw new Error('New host content is not allowed.')
        // A block placeholder is represented by its own paragraph, never inserted into inline content.
        if (node.type.name === 'paragraph' && originals.has(node.textContent) && !originals.get(node.textContent)!.isInline) return [originals.get(node.textContent)!]
        if (node.isText) {
          const parts: Node[] = []; let at = 0
          for (const match of node.text!.matchAll(tokens)) {
            if (match.index! > at) parts.push(editor.schema.text(node.text!.slice(at, match.index), node.marks))
            const original = originals.get(match[0])!
            if (!original.isInline) throw new Error('A block placeholder moved into text.')
            parts.push(original); at = match.index! + match[0].length
          }
          if (at < node.text!.length) parts.push(editor.schema.text(node.text!.slice(at), node.marks))
          return parts
        }
        const children: Node[] = []; node.forEach(child => children.push(...restore(child)))
        return [node.isLeaf ? node : node.copy(Fragment.from(children))]
      }
      const document = restore(parseMarkdownContent(editor, result))[0]!
      document.check()
      return document.toJSON()
    },
  }
}
