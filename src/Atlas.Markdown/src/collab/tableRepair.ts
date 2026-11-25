import { Extension } from '@tiptap/core'
import type { Node as ProseMirrorNode } from '@tiptap/pm/model'
import { Plugin, PluginKey } from '@tiptap/pm/state'
import { TableMap } from '@tiptap/pm/tables'
import type { EditorView } from '@tiptap/pm/view'
import { ySyncPluginKey } from '@tiptap/y-tiptap'
import * as Y from 'yjs'
import { isRemoteChange, TABLE_REPAIR } from '../core/tableMarkdown'

interface SyncBinding {
  doc: Y.Doc
  type: Y.XmlFragment
}

const bindingOf = (state: EditorView['state']) => (ySyncPluginKey.getState(state) as { binding?: SyncBinding } | undefined)?.binding

const author = (type: unknown) => type instanceof Y.XmlElement ? type._item?.id.client : undefined

/**
 * The Yjs element of the table at `pos`, by its path of child indexes (tables sit in block containers, whose Yjs children are
 * their blocks one to one). Not by the binding's node identity: a remote change replaces only the blocks it changed (y-tiptap
 * 3.3), so an untouched table keeps its node and a plugin's local addition to a new one (cell ids) gives it another.
 */
function tableElementAt(binding: SyncBinding, doc: ProseMirrorNode, pos: number, table: ProseMirrorNode): Y.XmlElement | undefined {
  const $pos = doc.resolve(pos)
  let type: Y.XmlFragment | Y.XmlElement = binding.type
  for (let depth = 0; depth <= $pos.depth; depth++) {
    const child: unknown = type.get($pos.index(depth))
    if (!(child instanceof Y.XmlElement)) return undefined
    type = child
  }
  return type instanceof Y.XmlElement && type.nodeName === table.type.name && type.length === table.childCount ? type : undefined
}

/**
 * The Yjs client ids that may repair a table, or null when any client may. A repair inserts cells and Yjs keeps every concurrent
 * insert, so it must come from one collaborator, and every collaborator must pick the same one from the merged document alone.
 * A merge breaks a table when a row was inserted (added, moved, pasted) while columns were added or deleted: that row is short or
 * long against the rest, and its author, who has just made it, repairs. Other repairs (a cell's type or alignment, a column
 * width) change attributes and agree when several clients make them. A table no live author can repair is repaired by the next
 * local edit of it, as without collaboration.
 */
function repairers(table: ProseMirrorNode, element: Y.XmlElement): number[] | null {
  const map = TableMap.get(table)
  const missing = new Array<number>(map.height).fill(0)
  for (const problem of map.problems ?? []) if (problem.type === 'missing') missing[problem.row]! += problem.n
  const widths = missing.map(n => map.width - n)
  const count = (width: number) => widths.filter(other => other === width).length
  // The width most rows have; the rows that differ are the ones the merge broke.
  const common = widths.reduce((best, width) => count(width) > count(best) ? width : best, widths[0] ?? 0)
  const rows = element.toArray()
  const odd = widths.flatMap((width, row) => width === common ? [] : [author(rows[row])]).filter(client => client !== undefined)
  if (odd.length) return odd
  // Header and alignment normalization (markdown-safe): a cell in the wrong role is replaced, so its author repairs it.
  const wrong: number[] = []
  table.forEach((row, _offset, index) => row.forEach((cell, _cellOffset, column) => {
    const client = cell.type.name !== (index === 0 ? 'tableHeader' : 'tableCell') ? author((rows[index] as Y.XmlElement | undefined)?.get(column)) : undefined
    if (client !== undefined) wrong.push(client)
  }))
  return wrong.length ? wrong : null
}

/** Repairs the tables remote changes broke on exactly one collaborator, as a local change that reaches Yjs at once. */
export const CollaborationTableRepair = Extension.create({
  name: 'markdownCollaborationTableRepair',
  addProseMirrorPlugins() {
    let view: EditorView | null = null
    let scheduled = false
    // Every table, after every remote change, as when y-tiptap replaced the whole document: a repair its author could not make
    // yet (the change that broke it came before the one it needs) is made on a later change, even one elsewhere.
    const repair = () => {
      scheduled = false
      const binding = view && !view.isDestroyed ? bindingOf(view.state) : undefined
      if (!view || !binding) return
      const positions: number[] = []
      view.state.doc.descendants((node, pos) => {
        if (node.type.spec.tableRole !== 'table') return !node.isTextblock
        const element = tableElementAt(binding, view!.state.doc, pos, node)
        if (element) {
          const clients = repairers(node, element)
          if (!clients || Math.max(...clients) === binding.doc.clientID) positions.push(pos)
        }
        return false
      })
      const tr = view.state.tr.setMeta(TABLE_REPAIR, positions)
      // Only when a repair answers the request.
      if (positions.length && view.state.applyTransaction(tr).transactions.length > 1) view.dispatch(tr)
    }
    return [new Plugin({
      key: new PluginKey('markdownCollaborationTableRepair'),
      view(editorView) {
        view = editorView
        return { destroy: () => { view = null } }
      },
      appendTransaction(transactions, _oldState, state) {
        if (scheduled || !bindingOf(state) || !transactions.some(isRemoteChange)) return null
        let tables = false
        state.doc.descendants(node => { tables ||= node.type.spec.tableRole === 'table'; return !tables && !node.isTextblock })
        if (!tables) return null
        // Once y-prosemirror has rendered the change: a repair appended to it would never be written to Yjs.
        scheduled = true
        queueMicrotask(repair)
        return null
      },
    })]
  },
})
