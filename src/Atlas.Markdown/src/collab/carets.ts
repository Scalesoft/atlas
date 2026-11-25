import { Extension, type Editor } from '@tiptap/core'
import { Plugin, type EditorState } from '@tiptap/pm/state'
import { Decoration, DecorationSet } from '@tiptap/pm/view'
import { absolutePositionToRelativePosition, isStructuralTransaction, relativePositionToAbsolutePosition, yCursorPluginKey, ySyncPluginKey } from '@tiptap/y-tiptap'
import * as Y from 'yjs'
import type { Awareness } from 'y-protocols/awareness'
import { flushDomSelection } from '../core/internal'

export interface CaretUser { name: string; color: string; [field: string]: unknown }
interface CaretState { user?: Partial<CaretUser>; cursor?: { anchor: unknown; head: unknown } | null }
interface Changes { added: number[]; updated: number[]; removed: number[] }

const validColor = /^#[0-9a-fA-F]{6}$/
const colorOf = (user: Partial<CaretUser>) => typeof user.color === 'string' && validColor.test(user.color) ? user.color : 'transparent'

function caretElement(user: CaretUser): HTMLElement {
  const caret = document.createElement('span')
  caret.classList.add('collaboration-carets__caret')
  caret.style.borderColor = colorOf(user)
  const label = document.createElement('div')
  label.classList.add('collaboration-carets__label')
  label.style.backgroundColor = colorOf(user)
  label.textContent = user.name
  caret.append(label)
  return caret
}

/** The binding whose positions resolve: none while it shows a snapshot or pauses on content it cannot show. */
function mappable(state: EditorState) {
  const sync = ySyncPluginKey.getState(state)
  return sync?.binding && sync.binding.invalidContentError === null && sync.snapshot == null && sync.prevSnapshot == null ? sync : null
}

type Cursor = NonNullable<CaretState['cursor']>

/**
 * Every collaborator's caret and selection (y-tiptap's createDecorations). A caret often arrives before the edit it points into: until
 * that edit arrives its position does not resolve, and the caret stays where its last resolvable position (`shown`, per client) is now,
 * instead of blinking out and in. (Mapping the old decoration would not do: a remote change replaces the whole block it changed.)
 */
function decorations(state: EditorState, awareness: Awareness, shown: Map<number, Cursor>): DecorationSet {
  const sync = mappable(state)
  if (!sync || sync.binding.mapping.size === 0) return DecorationSet.empty
  const found: Decoration[] = []
  const max = Math.max(state.doc.content.size - 1, 0)
  const resolve = (position: unknown) =>
    relativePositionToAbsolutePosition(sync.doc, sync.type, Y.createRelativePositionFromJSON(position), sync.binding.mapping)
  const present = new Set<number>()
  awareness.getStates().forEach((value, clientId) => {
    const { user = {}, cursor: current } = value as CaretState
    if (clientId === sync.doc.clientID || current == null) return
    present.add(clientId)
    let cursor = current, anchor = resolve(cursor.anchor), head = resolve(cursor.head)
    if (anchor === null || head === null) {
      const last = shown.get(clientId)
      if (!last) return
      cursor = last; anchor = resolve(cursor.anchor); head = resolve(cursor.head)
      if (anchor === null || head === null) return
    }
    shown.set(clientId, cursor)
    const person: CaretUser = { ...user, name: typeof user.name === 'string' ? user.name : `User: ${clientId}`, color: colorOf(user) }
    const at = Math.min(head, max), from = Math.min(anchor, at, max), to = Math.min(Math.max(anchor, head), max)
    found.push(Decoration.widget(at, () => caretElement(person), { key: String(clientId), side: 10, clientId }))
    if (person.color !== 'transparent') {
      found.push(Decoration.inline(from, to, { style: `background-color: ${person.color}70`, class: 'ProseMirror-yjs-selection' },
        { inclusiveEnd: true, inclusiveStart: false, clientId }))
    }
  })
  for (const clientId of shown.keys()) if (!present.has(clientId)) shown.delete(clientId)
  return DecorationSet.create(state.doc, found)
}

const sameCursor = (published: CaretState['cursor'], anchor: Y.RelativePosition, head: Y.RelativePosition) => published != null
  && Y.compareRelativePositions(Y.createRelativePositionFromJSON(published.anchor), anchor)
  && Y.compareRelativePositions(Y.createRelativePositionFromJSON(published.head), head)

/**
 * Collaborators' carets and the own caret published through awareness, as y-tiptap's yCursorPlugin, with Atlas's rules:
 * - a caret that arrives before its edit keeps its place (above);
 * - only collaborators' changes redraw, at most once per animation frame (a hidden tab catches up when shown); the selection the browser
 *   changed but has not reported yet (a click or key right before) is read first, else the redraw writes the older one back to the DOM;
 * - the own caret is published after the first key or pointer press, not when the page is opened and focused programmatically.
 */
export function collaborationCaretsPlugin(editor: Editor, awareness: Awareness): Plugin<DecorationSet> {
  const shown = new Map<number, Cursor>()
  // Kept across the plugin views of one editor: registering a plugin later recreates them, and that must neither retract the published
  // caret nor wait for another key.
  let published: CaretState['cursor'] = null
  let interacted = false
  return new Plugin<DecorationSet>({
    key: yCursorPluginKey,
    state: {
      init: (_, state) => decorations(state, awareness, shown),
      apply(tr, previous, oldState, state) {
        const remote = ySyncPluginKey.getState(state)?.isChangeOrigin
        if (tr.docChanged && !remote && isStructuralTransaction(tr, oldState.doc)) return DecorationSet.empty
        if (remote || tr.getMeta(yCursorPluginKey)?.awarenessUpdated) return decorations(state, awareness, shown)
        return tr.docChanged ? previous.map(tr.mapping, tr.doc) : previous
      },
    },
    props: { decorations: state => yCursorPluginKey.getState(state) },
    view(view) {
      let frame: number | ReturnType<typeof setTimeout> | null = null
      const redraw = () => {
        frame = null
        if (view.isDestroyed) return
        flushDomSelection(editor)
        view.dispatch(view.state.tr.setMeta(yCursorPluginKey, { awarenessUpdated: true }))
      }
      const changed = ({ added, updated, removed }: Changes) => {
        if (frame !== null || [...added, ...updated, ...removed].every(client => client === awareness.clientID)) return
        frame = typeof requestAnimationFrame === 'function' ? requestAnimationFrame(redraw) : setTimeout(redraw)
      }
      const publish = () => {
        const sync = mappable(view.state), local = awareness.getLocalState() as CaretState | null
        if (local === null) return
        if (!sync || !view.hasFocus() || !interacted) {
          // Collaborators should not see a caret this person no longer moves; only what this view published is cleared.
          if (published !== null && local.cursor === published) awareness.setLocalStateField('cursor', null)
          published = null
          return
        }
        const { anchor, head } = view.state.selection
        const relative = (position: number) => absolutePositionToRelativePosition(position, sync.type, sync.binding.mapping)
        const next = { anchor: relative(anchor), head: relative(head) }
        if (local.cursor === published && sameCursor(published, next.anchor, next.head)) return
        published = next
        awareness.setLocalStateField('cursor', next)
      }
      const interact = () => {
        if (interacted) return
        interacted = true
        view.dom.removeEventListener('keydown', interact, true)
        view.dom.removeEventListener('pointerdown', interact, true)
        // After the key's transaction or the click's selection took place.
        setTimeout(() => { if (!view.isDestroyed) publish() })
      }
      if (!interacted) {
        view.dom.addEventListener('keydown', interact, true)
        view.dom.addEventListener('pointerdown', interact, true)
      }
      view.dom.addEventListener('focusin', publish)
      view.dom.addEventListener('focusout', publish)
      awareness.on('change', changed)
      return {
        update: publish,
        destroy() {
          if (frame !== null) typeof requestAnimationFrame === 'function' && typeof frame === 'number' ? cancelAnimationFrame(frame) : clearTimeout(frame)
          view.dom.removeEventListener('keydown', interact, true)
          view.dom.removeEventListener('pointerdown', interact, true)
          view.dom.removeEventListener('focusin', publish)
          view.dom.removeEventListener('focusout', publish)
          awareness.off('change', changed)
          // Only when the editor goes (its view is destroyed right after its plugin views), not when a plugin registered later
          // recreates them.
          queueMicrotask(() => {
            if (!view.isDestroyed || published === null) return
            if ((awareness.getLocalState() as CaretState | null)?.cursor === published) awareness.setLocalStateField('cursor', null)
            published = null
          })
        },
      }
    },
  })
}

/**
 * The collaboration carets of an editor (in place of Tiptap's CollaborationCaret, whose name and `provider` option it keeps, so a host
 * finds the awareness it publishes to). The user is published as the awareness `user` field.
 */
export const CollaborationCarets = Extension.create<{ provider: { awareness: Awareness } | null; user: CaretUser | null }>({
  name: 'collaborationCaret',
  priority: 999,
  addOptions: () => ({ provider: null, user: null }),
  addProseMirrorPlugins() {
    const awareness = this.options.provider?.awareness
    if (!awareness) throw new Error('CollaborationCarets needs the provider whose awareness it publishes to.')
    if (this.options.user) awareness.setLocalStateField('user', this.options.user)
    return [collaborationCaretsPlugin(this.editor, awareness)]
  },
})
