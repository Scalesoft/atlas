import type { Editor } from '@tiptap/core'
import type { Transaction } from '@tiptap/pm/state'
import { Mapping } from '@tiptap/pm/transform'

/** Quiet time before the outline reads the headings again (as a `watchDocument` block, features/modules/block.ts). */
const OUTLINE_IDLE_MS = 100
import { Decoration, DecorationSet } from '@tiptap/pm/view'
import { outlineHighlights } from '../engine/outlineHighlight'
import { markdownChangeMapping } from '../core/changeMapping'
import { outlineStrings } from './strings/outline'

/** View-only navigation; never serialized into the document or shared with peers. */
export type MarkdownOutlineMode = 'hidden' | 'floating' | 'panel'
export interface MarkdownOutlineOptions {
  mode: MarkdownOutlineMode
  /** Initially pin the floating outline. Panel mode always docks at the top right. */
  pinned?: boolean
  /** Initially collapse panel mode to its trigger. */
  collapsed?: boolean
  preview?: boolean
}
export interface OutlineHeading { pos: number; level: number; title: string; element: HTMLElement; headingId?: string }

/**
 * Where DocumentOutline reads its headings: a live editor or a statically rendered document.
 * Positions are opaque to the outline; only the source that produced them can map them.
 */
export interface OutlineSource {
  /** The rendered document the headings belong to (the editor's view.dom or the renderer's content). */
  readonly root: HTMLElement
  /** Owner of the outline's popover; one active popover per owner, as for an editor. */
  readonly popoverOwner: object
  collect(): OutlineHeading[]
  createHighlight(): { set(heading: OutlineHeading | null): void; destroy(): void }
  /**
   * `changed` runs when headings may have moved, with a position mapping when the source has one;
   * `redrawn` when only the rendering changed (the live preview mirrors it).
   */
  subscribe(changed: (map?: (pos: number) => number) => void, redrawn: () => void): () => void
}

export function editorOutlineSource(editor: Editor): OutlineSource {
  return {
    root: editor.view.dom,
    popoverOwner: editor,
    collect: () => collectOutlineHeadings(editor),
    createHighlight() {
      const highlight = createOutlineHighlight(editor)
      return { set: heading => highlight.set(heading?.pos ?? null), destroy: () => highlight.destroy() }
    },
    subscribe(changed, redrawn) {
      // Typing changes the document many times a second: the headings are read once it pauses, with every change since mapped
      // (J12.6: no scan per keystroke, the outline in idle ≤ 150 ms).
      let mapping: Mapping | undefined
      let timer: ReturnType<typeof setTimeout> | undefined
      const settle = () => { const map = mapping!; mapping = undefined; timer = undefined; changed(pos => map.map(pos)) }
      const onTransaction = ({ transaction }: { transaction: Transaction }) => {
        if (!transaction.docChanged) return redrawn()
        ;(mapping ??= new Mapping()).appendMapping(transaction.mapping)
        clearTimeout(timer); timer = setTimeout(settle, OUTLINE_IDLE_MS)
      }
      editor.on('transaction', onTransaction)
      return () => { clearTimeout(timer); editor.off('transaction', onTransaction) }
    },
  }
}

export interface StaticOutlineSource extends OutlineSource {
  /** The renderer replaced the document; collect again. */
  changed(): void
}

/** The words of a rendered heading: an inline component inside it carries its own `<style>`, and an editor's widget (a collaborator's caret with its name) is drawn, not written; neither is title. */
function headingText(element: HTMLElement): string {
  const skip = 'style,script,.ProseMirror-widget'
  if (!element.querySelector(skip)) return element.textContent?.trim() ?? ''
  const copy = element.cloneNode(true) as HTMLElement
  copy.querySelectorAll(skip).forEach(node => node.remove())
  return copy.textContent?.trim() ?? ''
}

/** Headings of a statically rendered document; the highlight is the attribute the editor decorates with. */
export function staticOutlineSource(root: HTMLElement): StaticOutlineSource {
  const listeners = new Set<(map?: (pos: number) => number) => void>()
  return {
    root,
    popoverOwner: root,
    collect: () => [...root.querySelectorAll<HTMLElement>('h1,h2,h3,h4,h5,h6')]
      .filter(element => !element.closest('[data-md-module]'))
      .map((element, pos) => ({ pos, level: Number(element.tagName.slice(1)), title: headingText(element) || outlineStrings.untitledHeading, element,
        ...(element.dataset.headingId ? { headingId: element.dataset.headingId } : {}),
      })),
    createHighlight() {
      let target: HTMLElement | null = null
      const set = (heading: OutlineHeading | null) => {
        if (target === (heading?.element ?? null)) return
        target?.removeAttribute('data-md-outline-target')
        target = heading?.element ?? null
        target?.setAttribute('data-md-outline-target', 'true')
      }
      return { set, destroy: () => set(null) }
    },
    subscribe(changed) {
      listeners.add(changed)
      return () => { listeners.delete(changed) }
    },
    changed() { for (const listener of [...listeners]) listener() },
  }
}

const highlightTargets = new WeakMap<Editor, Map<object, number | null>>()

/** Outline mount/unmount owns decorations without reconfiguring active plugin views. */
export function createOutlineHighlight(editor: Editor) {
  let targets = highlightTargets.get(editor)
  if (!targets) {
    targets = new Map<object, number | null>()
    highlightTargets.set(editor, targets)
  }
  const sharedTargets = targets
  outlineHighlights.set(editor, {
    map(tr) {
      const mapping = tr.docChanged ? markdownChangeMapping(tr) : null
      if (mapping) for (const [owner, target] of sharedTargets) {
        if (target === null) continue
        const mapped = mapping.mapResult(target)
        sharedTargets.set(owner, !mapped.deleted && tr.doc.nodeAt(mapped.pos)?.type.name === 'heading' ? mapped.pos : null)
      }
    },
    decorations(state) {
      const decorations: Decoration[] = []
      for (const pos of sharedTargets.values()) {
        const node = pos === null ? null : state.doc.nodeAt(pos)
        if (node && pos !== null) decorations.push(Decoration.node(pos, pos + node.nodeSize, { 'data-md-outline-target': 'true' }))
      }
      return DecorationSet.create(state.doc, decorations)
    },
  })
  const owner = {}
  targets.set(owner, null)
  let destroyed = false
  const redraw = () => {
    // SAME state: metadata transactions can trigger third-party appendTransaction hooks.
    if (!editor.isDestroyed) editor.view.updateState(editor.state)
  }
  return {
    set(pos: number | null) {
      if (destroyed || editor.isDestroyed || targets.get(owner) === pos) return
      targets.set(owner, pos)
      redraw()
    },
    destroy() {
      if (destroyed) return
      destroyed = true
      const target = targets.get(owner)
      targets.delete(owner)
      if (!targets.size) outlineHighlights.delete(editor)
      if (target !== null) redraw()
    },
  }
}

export function collectOutlineHeadings(editor: Editor): OutlineHeading[] {
  const headings: OutlineHeading[] = []
  if (editor.isDestroyed) return headings
  editor.state.doc.descendants((node, pos) => {
    if (node.isAtom) return false
    if (node.type.name !== 'heading') return
    const element = editor.view.nodeDOM(pos) as HTMLElement | null
    if (!element?.matches?.('h1,h2,h3,h4,h5,h6') || element.closest('[data-md-module]')) return
    headings.push({ pos, level: Number(node.attrs.level), title: node.textContent.trim() || outlineStrings.untitledHeading, element,
      ...(typeof node.attrs.headingId === 'string' && node.attrs.headingId ? { headingId: node.attrs.headingId } : {}),
    })
  })
  return headings
}

/** Stable pointer targets: only the artwork moves, not the buttons under the pointer. */
export function outlineWave(level: number, distance: number | null, current = false, pitch = 12) {
  const influence = distance === null ? 0 : Math.exp(-Math.pow(distance / 1.6, 2))
  const width = 26 - (Math.max(1, Math.min(6, level)) - 1) * 3
  const baseHeight = Math.min(2 + Number(current), pitch * .55)
  const height = baseHeight + (16 - baseHeight) * influence ** 3
  const reveal = Math.max(0, Math.min(1, (height - 10) / 5))
  return {
    width: width + (Math.max(32, width * 1.75) - width) * influence + Number(current) * 2,
    height, opacity: reveal * reveal * (3 - 2 * reveal),
    x: -influence * 6.75,
    // Spread the artwork around a dense hover without moving its pointer targets.
    y: distance === null ? 0 : Math.sign(distance) * (Math.min(Math.abs(distance), 1) * influence * 4 + (1 - Math.exp(-Math.abs(distance) / 1.1)) * Math.max(0, 12 - pitch) * 1.25),
  }
}
