import { Plugin, PluginKey, type EditorState } from '@tiptap/pm/state'
import { Decoration, DecorationSet } from '@tiptap/pm/view'

/**
 * Local presentation only: decorations never enter the document, JSON, Markdown, history or Yjs.
 * The runtime replaces the whole set through a meta transaction (no document change, no history entry)
 * and only when it actually differs, so a default suggestion costs no transaction at all.
 */
export const assistDecorationsKey = new PluginKey<DecorationSet>('markdownAssistDecorations')
export const assistMeta = 'markdownAssist'

export function assistDecorationsPlugin(): Plugin<DecorationSet> {
  return new Plugin<DecorationSet>({
    key: assistDecorationsKey,
    state: {
      init: () => DecorationSet.empty,
      apply(transaction, set) {
        const next = transaction.getMeta(assistDecorationsKey) as DecorationSet | undefined
        return next ?? set.map(transaction.mapping, transaction.doc)
      },
    },
    props: { decorations: state => assistDecorationsKey.getState(state) },
  })
}

/** A comparable description of one decoration, so an unchanged set dispatches nothing. */
export interface AssistDecorationSpec {
  readonly key: string
  readonly decoration: Decoration
}

export function sameDecorations(state: EditorState, specs: readonly AssistDecorationSpec[]): boolean {
  const current = assistDecorationsKey.getState(state)?.find() ?? []
  if (current.length !== specs.length) return false
  const keyed = new Map(current.map(decoration => [`${(decoration.spec as { key?: string }).key}@${decoration.from}-${decoration.to}`, decoration]))
  return specs.every(spec => keyed.has(`${spec.key}@${spec.decoration.from}-${spec.decoration.to}`))
}

export const inline = (from: number, to: number, key: string, attributes: Record<string, string>) =>
  ({ key, decoration: Decoration.inline(from, to, attributes, { key, inclusiveStart: false, inclusiveEnd: false }) })
export const widget = (pos: number, key: string, render: () => HTMLElement, side: number) =>
  ({ key, decoration: Decoration.widget(pos, render, { key, side, ignoreSelection: true, stopEvent: () => true }) })
