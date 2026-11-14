import { Extension } from '@tiptap/core'
import type { Node as PMNode } from '@tiptap/pm/model'
import { Plugin, PluginKey, TextSelection, type EditorState } from '@tiptap/pm/state'
import { ySyncPluginKey } from '@tiptap/y-tiptap'
import * as Y from 'yjs'
import { markdownRemoteRemovalEvent, type MarkdownRemoteRemovalDetail } from '../core/inputErrors'
import { dispatchMarkdownHistoryOperation } from '../engine/historyBoundary'
import { collaborationStrings } from '../ui/strings/collaborationAddons'

/** The part of the presence channel this needs: who is who. */
export interface RemoteRemovalAwareness { getStates(): Map<number, Record<string, unknown>> }
export interface RemoteRemovalOptions { awareness: () => RemoteRemovalAwareness | null | undefined }

interface SyncBinding { doc: Y.Doc; type: Y.XmlFragment }

/** The Yjs elements around the caret, outermost first, with the nodes they were before a remote change. */
interface Watched { transaction: Y.Transaction; types: Y.XmlElement[]; nodes: PMNode[]; caret: EditorState['selection']['$head'] }

function caretPath(binding: SyncBinding, state: EditorState, transaction: Y.Transaction): Watched | null {
  const { $head } = state.selection, types: Y.XmlElement[] = [], nodes: PMNode[] = []
  let parent: Y.XmlFragment = binding.type
  for (let depth = 0; depth < $head.depth; depth++) {
    const child = parent.get($head.index(depth)), node = $head.node(depth + 1)
    // Blocks map 1:1 to Yjs elements; anything else (a mismatch in the middle of a sync) is not guessed at.
    if (!(child instanceof Y.XmlElement) || child.nodeName !== node.type.name) return null
    types.push(child); nodes.push(node); parent = child
  }
  return types.length ? { transaction, types, nodes, caret: $head } : null
}

/** Where a live Yjs element is in the document now, by its index path (blocks map 1:1); null if it is gone or does not match. */
function locate(root: Y.XmlFragment, doc: PMNode, type: Y.AbstractType<unknown>): { pos: number; node: PMNode } | null {
  const path: Y.XmlElement[] = []
  for (let at = type; at !== root;) {
    const item = at._item
    if (!item || item.deleted || !(at instanceof Y.XmlElement)) return null
    path.unshift(at); at = item.parent as Y.AbstractType<unknown>
  }
  let node = doc, pos = -1
  for (const element of path) {
    const index = (element._item!.parent as Y.XmlFragment).toArray().indexOf(element)
    if (index < 0 || index >= node.childCount || node.child(index).type.name !== element.nodeName) return null
    let start = pos + 1
    for (let child = 0; child < index; child++) start += node.child(child).nodeSize
    pos = start; node = node.child(index)
  }
  return { pos, node }
}

/** Right after the nearest sibling that survived the removal, else at the start of its parent; null when that is gone too. */
function nearestPlace(binding: SyncBinding, doc: PMNode, removed: Y.XmlElement): number | null {
  let left = removed._item!.left
  while (left && (left.deleted || !(left.content instanceof Y.ContentType))) left = left.left
  const place = locate(binding.type, doc, left ? (left.content as Y.ContentType).type : removed._item!.parent as Y.AbstractType<unknown>)
  return place && (left ? place.pos + place.node.nodeSize : place.pos + 1)
}

/** Who removed it: the one collaborator who wrote in that change, else the only one present; Yjs itself does not record it. */
function remover(transaction: Y.Transaction, own: number, awareness: RemoteRemovalAwareness | null | undefined) {
  const states = awareness?.getStates()
  const name = (client: number) => {
    const user = states?.get(client)?.user as { name?: unknown } | undefined
    return typeof user?.name === 'string' && user.name ? user.name : null
  }
  const writers = [...transaction.afterState].filter(([client, clock]) => client !== own && clock !== (transaction.beforeState.get(client) ?? 0))
  if (writers.length === 1 && name(writers[0]![0])) return name(writers[0]![0])
  const present = [...(states?.keys() ?? [])].filter(client => client !== own && name(client))
  return present.length === 1 ? name(present[0]!) : null
}

/** A block with the same identity (a heading's, a requirement's id) is still there: a move, not a removal (J10.5). */
function stillThere(doc: PMNode, node: PMNode) {
  const identity = node.attrs.id ?? node.attrs.headingId
  if (identity == null) return false
  let found = false
  doc.descendants(other => { found ||= other.type === node.type && (other.attrs.id ?? other.attrs.headingId) === identity; return !found })
  return found
}

/**
 * Contract J10.6, VS5: a colleague's change that removes the block (or the section, the list, the callout) holding my caret also
 * removes what I typed there that they had not received yet; Yjs drops text inserted into a deleted element. The block as I had it
 * is kept and a quiet notice (`markdownRemoteRemovalEvent` on the editor's root) names who removed it, with „Vrátit“: the block
 * comes back with my text right after its nearest surviving sibling, as one local undo step. The caret itself goes to the nearest
 * surviving place (y-tiptap's relative selection). Nothing is written until „Vrátit“.
 */
export const CollaborationRemoteRemoval = Extension.create<RemoteRemovalOptions>({
  name: 'markdownCollaborationRemoteRemoval',
  addOptions: () => ({ awareness: () => null }),
  addProseMirrorPlugins() {
    const editor = this.editor, options = this.options
    return [new Plugin({
      key: new PluginKey('markdownCollaborationRemoteRemoval'),
      view(view) {
        const binding = (ySyncPluginKey.getState(view.state) as { binding?: SyncBinding } | undefined)?.binding
        if (!binding) return {}
        let watched: Watched | null = null
        // A remote change only; a local one inside it (a repair the binding writes back) leaves the watch to the outer change.
        const before = (transaction: Y.Transaction) => {
          if (!transaction.local && editor.isEditable && !editor.isDestroyed) watched = caretPath(binding, view.state, transaction)
        }
        const after = (transaction: Y.Transaction) => {
          if (watched?.transaction !== transaction) return
          const { types, nodes, caret } = watched
          watched = null
          const unit = types.findIndex(type => type._item?.deleted)
          const node = nodes[unit]
          if (unit < 0 || !node?.textContent.trim() || editor.isDestroyed || stillThere(view.state.doc, node)) return
          const removed = types[unit]!, offset = caret.pos - caret.before(unit + 1)
          const restore = () => {
            if (editor.isDestroyed || !editor.isEditable) return false
            const { state } = editor, at = nearestPlace(binding, state.doc, removed) ?? (state.selection.$head.depth ? state.selection.$head.after(1) : 0)
            const tr = state.tr.insert(at, node)
            if (!tr.docChanged) return false
            tr.setSelection(TextSelection.near(tr.doc.resolve(Math.min(at + offset, tr.doc.content.size))))
            dispatchMarkdownHistoryOperation(editor, tr.scrollIntoView())
            return true
          }
          const detail: MarkdownRemoteRemovalDetail = { message: collaborationStrings.remoteRemoval(remover(transaction, binding.doc.clientID, options.awareness())), restore }
          const CustomEventConstructor = view.dom.ownerDocument.defaultView?.CustomEvent
          if (CustomEventConstructor) view.dom.dispatchEvent(new CustomEventConstructor(markdownRemoteRemovalEvent, { bubbles: true, detail }))
        }
        binding.doc.on('beforeTransaction', before)
        binding.doc.on('afterTransaction', after)
        return { destroy: () => { binding.doc.off('beforeTransaction', before); binding.doc.off('afterTransaction', after) } }
      },
    })]
  },
})
