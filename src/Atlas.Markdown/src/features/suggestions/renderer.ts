import type { Editor, Range } from '@tiptap/core'
import type { PluginKey, Transaction } from '@tiptap/pm/state'
import { exitSuggestion, type SuggestionOptions, type SuggestionProps } from '@tiptap/suggestion'
import { closeMarkdownHistory } from '../../engine/historyBoundary'
import { createSuggestionList, type MarkdownSuggestionItem } from './list'
import { createSuggestionPopup, type SuggestionPopup } from './popup'
import { createSuggestionSearch, type SuggestionSearchOptions } from './search'
import { suggestionsStrings } from '../../ui/strings/suggestions'

export interface MarkdownSuggestionRendererOptions<T> {
  pluginKey: PluginKey
  label: string
  /** Visible heading of a command list (the slash menu's "Vložit"). */
  heading?: string
  mapItem: (item: T) => MarkdownSuggestionItem
  testId?: string
  emptyText?: string
  hintText?: string
  errorText?: string
  /** With a source, configure upstream items: () => []; this renderer owns async work. */
  source?: Omit<SuggestionSearchOptions<T>, 'onChange'>
  /**
   * The items for a query, synchronously. A key (Enter, Tab, an arrow) that arrives before the list caught up with the typed
   * text then acts on the items of what is typed now, never on the rows of an earlier query.
   */
  itemsNow?: (query: string, editor: Editor) => T[]
  /** Runs a picked item with the live range of the typed text (default: the suggestion's own command with the range it last rendered). */
  command?: (item: T, context: { editor: Editor; range: Range }) => void
}
const active = new WeakMap<Editor, Map<PluginKey, () => void>>()

/** Mirrors upstream collaboration origin detection without importing the optional Yjs runtime. */
export function shouldShowMarkdownSuggestion({ transaction }: { transaction: Transaction }): boolean {
  return !transaction.getMeta('y-sync$')
}

export function closeMarkdownSuggestion(editor: Editor, key: PluginKey): void {
  const dismiss = active.get(editor)?.get(key)
  if (dismiss) dismiss()
  else if (!editor.isDestroyed) exitSuggestion(editor.view, key)
}

function caretRect(editor: Editor, range: Range, fallback?: (() => DOMRect | null) | null): DOMRect {
  const doc = editor.view.dom.ownerDocument, Rect = doc.defaultView?.DOMRect ?? DOMRect
  try {
    const selection = doc.getSelection()
    if (selection?.rangeCount && selection.anchorNode && editor.view.dom.contains(selection.anchorNode)) {
      const rect = selection.getRangeAt(0).getBoundingClientRect()
      // WebKit may expose an empty rectangle for a collapsed DOM Range even
      // when its caret is visible. Do not turn that into a fake anchor at (0,0).
      if (rect.height > 0 && Number.isFinite(rect.left) && Number.isFinite(rect.top)) {
        return new Rect(rect.left, rect.top, Math.max(1, rect.width), rect.height)
      }
    }
    const { left, right, top, bottom } = editor.view.coordsAtPos(range.to)
    return new Rect(left, top, Math.max(1, right - left), Math.max(1, bottom - top))
  } catch { return fallback?.() ?? new Rect() }
}

/** Shared lifecycle for slash, people, pages and emoji. Never moves typing focus. */
export function createMarkdownSuggestionRenderer<T>(options: MarkdownSuggestionRendererOptions<T>): NonNullable<SuggestionOptions<T, T>['render']> {
  return () => {
    let props: SuggestionProps<T, T> | undefined, popup: SuggestionPopup | undefined
    let list: ReturnType<typeof createSuggestionList> | undefined
    let search: ReturnType<typeof createSuggestionSearch<T>> | undefined
    let values: T[] = [], release: (() => void) | undefined
    let revision = 0
    // The query whose items the list shows; `undefined` while it shows none of them (loading, a status).
    let shownQuery: string | undefined
    const live = (editor: Editor) => {
      const state = options.pluginKey.getState(editor.state)
      return state?.active ? { range: state.range as Range, query: (state.query ?? '') as string } : undefined
    }
    const cleanup = () => {
      revision++; search?.destroy(); search = undefined; values = []
      popup?.destroy(); popup = undefined
      list?.destroy(); list = undefined
      release?.(); release = undefined; props = undefined
    }
    const dismiss = () => { const editor = props?.editor; cleanup(); if (editor && !editor.isDestroyed) exitSuggestion(editor.view, options.pluginKey) }
    const valid = (next: SuggestionProps<T, T>) => {
      if (next.editor.isDestroyed || !next.editor.isEditable) return false
      const state = options.pluginKey.getState(next.editor.state)
      return !!state?.active && state.range.from === next.range.from && state.range.to === next.range.to
        && (state.query == null || state.query === next.query)
    }
    const present = (items: T[], query?: string) => { values = items; shownQuery = query; list?.update(items.map(options.mapItem)) }
    const update = (next: SuggestionProps<T, T>) => {
      if (!valid(next)) { dismiss(); return }
      props = next
      popup?.setReferenceRect(() => caretRect(next.editor, next.range, next.clientRect))
      if (search) search.query(next.query)
      // Upstream renders an empty, loading list before its items arrive; that is no answer to the query yet.
      else present(next.items, next.loading ? undefined : next.query)
    }
    /** Brings the list up to what is typed now before a key acts on it (fast typing, then Enter at once). */
    const catchUp = () => {
      const now = props && options.itemsNow ? live(props.editor) : undefined
      if (now && now.query !== shownQuery) present(options.itemsNow!(now.query, props!.editor), now.query)
    }
    return {
      onStart(next) {
        if (!valid(next)) return
        cleanup(); props = next
        const editor = next.editor, root = editor.view.dom, doc = root.ownerDocument
        const attributes = ['aria-controls', 'aria-expanded', 'aria-activedescendant', 'aria-autocomplete']
        const previous = new Map(attributes.map(name => [name, root.getAttribute(name)]))
        list = createSuggestionList({ document: doc, label: options.label, heading: options.heading, testId: options.testId, emptyText: options.emptyText,
          onActiveChange: id => { if (id) root.setAttribute('aria-activedescendant', id); else root.removeAttribute('aria-activedescendant') },
          onSelect: id => {
            const current = props, now = live(editor)
            if (!current || !now || editor.view.composing || (!options.command && !valid(current)) || editor.isDestroyed || !editor.isEditable) return
            const item = values.find(value => options.mapItem(value).id === id)
            if (!item || options.mapItem(item).disabled) return
            cleanup()
            editor.view.dispatch(closeMarkdownHistory(editor.state.tr).setMeta('addToHistory', false))
            if (options.command) options.command(item, { editor, range: now.range })
            else current.command(item)
            if (!editor.isDestroyed) {
              editor.view.dispatch(closeMarkdownHistory(editor.state.tr).setMeta('addToHistory', false))
              exitSuggestion(editor.view, options.pluginKey)
            }
          },
        })
        root.setAttribute('aria-controls', list.element.id); root.setAttribute('aria-expanded', 'true'); root.setAttribute('aria-autocomplete', 'list')
        const check = () => { if (!editor.isEditable) dismiss() }
        const observer = new (doc.defaultView?.MutationObserver ?? MutationObserver)(check)
        observer.observe(root, { attributes: true, attributeFilter: ['contenteditable'] })
        editor.on('update', check); editor.on('destroy', cleanup)
        // Capture dismissal before fullscreen. Tab moves the highlight (onKeyDown), so focus stays in the editor.
        const keyboard = (event: KeyboardEvent) => {
          if (event.isComposing || editor.view.composing) return
          if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); dismiss() }
        }
        root.addEventListener('keydown', keyboard, true)
        // Focus that goes elsewhere (the palette, a modal dialog) ends the suggestion: Esc there must not meet a menu left behind.
        const leave = (event: FocusEvent) => { const to = event.relatedTarget as Node | null; if (to && !root.contains(to) && !list?.element.contains(to)) dismiss() }
        root.addEventListener('focusout', leave)
        let sessions = active.get(editor); if (!sessions) { sessions = new Map(); active.set(editor, sessions) }
        sessions.set(options.pluginKey, dismiss)
        release = () => {
          observer.disconnect(); editor.off('update', check); editor.off('destroy', cleanup); root.removeEventListener('keydown', keyboard, true); root.removeEventListener('focusout', leave)
          for (const [name, value] of previous) { if (value === null) root.removeAttribute(name); else root.setAttribute(name, value) }
          if (sessions.get(options.pluginKey) === dismiss) sessions.delete(options.pluginKey)
        }
        popup = createSuggestionPopup({ editor, onDismiss: dismiss, contextElement: root, content: list.element, getReferenceRect: () => caretRect(editor, props?.range ?? next.range, props?.clientRect ?? next.clientRect) })
        if (options.source) {
          const current = revision
          search = createSuggestionSearch<T>({ ...options.source, onChange: state => {
            if (current !== revision || !props || !valid(props)) return
            values = []
            if (state.status === 'ready') present(state.items, props.query)
            else list?.setStatus(state.status, state.status === 'error' ? options.errorText ?? suggestionsStrings.searchFailed
              : state.status === 'idle' ? options.hintText ?? suggestionsStrings.startTypingHint : undefined,
            state.status === 'error' ? () => search?.retry() : undefined)
          } })
        }
        update(next)
      },
      onBeforeUpdate() { values = []; shownQuery = undefined; list?.setStatus('loading') },
      onUpdate: update,
      onKeyDown({ event }) {
        if (event.isComposing || props?.editor.view.composing) return false
        if (event.key === 'Escape') { dismiss(); return true }
        if (event.ctrlKey || event.metaKey || event.altKey) return false
        catchUp()
        // Tab takes the active row like Enter (P8, as the panel field and the app pickers do); Shift+Tab moves up. Neither leaves the editor.
        if (event.key === 'Tab') { list?.onKeyDown(new KeyboardEvent('keydown', { key: event.shiftKey ? 'ArrowUp' : 'Enter' })); return true }
        return list?.onKeyDown(event) ?? false
      },
      onExit: cleanup,
    }
  }
}
