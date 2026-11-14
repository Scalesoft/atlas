import type { Editor } from '@tiptap/core'
import { NodeSelection } from '@tiptap/pm/state'
import { computeSectionEnd } from '../core'
import type { MarkdownAssistTarget } from './types'

// Names of what the caret or the selection points at, for the menu row („AI · Sekce „Úvod““). An unknown element is „Prvek“.
const names: Readonly<Record<string, string>> = {
  paragraph: 'Odstavec', bulletList: 'Seznam', orderedList: 'Seznam', taskList: 'Seznam', table: 'Tabulka', blockquote: 'Citace', codeBlock: 'Kód',
  callout: 'Informační box', chart: 'Graf', mathBlock: 'Vzorec', mathInline: 'Vzorec', diagram: 'Diagram', contactCard: 'Kontakt', contactInline: 'Kontakt',
  image: 'Obrázek', attachment: 'Příloha', openapi: 'OpenAPI', youtrackIssue: 'Úkol', pageReference: 'Odkaz na stránku', requirement: 'Požadavek',
  tableOfContents: 'Obsah',
}

/**
 * Everything an AI action can act on at the editor's selection (P11), nearest first, the whole page last: the selected text or the
 * selected element (a table or callout selected by its grip is a block, as with the caret inside); else, at the caret,
 * a heading's section (up to the next heading of the same or a higher level) with the heading alone as its fallback, or the
 * top-level block (a paragraph, a whole list, a table).
 */
export function markdownAssistTargets(editor: Editor): MarkdownAssistTarget[] {
  const { selection, doc } = editor.state
  const targets: MarkdownAssistTarget[] = []
  if (selection instanceof NodeSelection) targets.push({ kind: selection.node.isAtom ? 'element' : 'block', from: selection.from, to: selection.to, label: names[selection.node.type.name] ?? 'Prvek' })
  else if (!selection.empty) targets.push({ kind: 'selection', from: selection.from, to: selection.to, label: 'Výběr' })
  else if (selection.$from.depth >= 1) {
    const index = selection.$from.index(0), block = doc.child(index), from = selection.$from.before(1)
    if (block.type.name === 'heading') {
      const title = block.textContent.trim() || 'bez názvu'
      targets.push({ kind: 'section', from, to: computeSectionEnd(doc, from, block.attrs.level ?? 1), label: `Sekce „${title}“` },
        { kind: 'block', from, to: from + block.nodeSize, label: `Nadpis „${title}“`, fallback: true })
    } else targets.push({ kind: 'block', from, to: from + block.nodeSize, label: names[block.type.name] ?? 'Blok' })
  }
  if (doc.content.size > 0) targets.push({ kind: 'page', from: 0, to: doc.content.size, label: 'Stránka' })
  return targets
}
