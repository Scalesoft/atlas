import { markdownStrings, addMarkdownReadActions, copyMarkdownText, zoomMarkdownBlock, createMarkdownModuleLoader, isMarkdownComposition, openMarkdownContextPopover, retainMarkdownDraft, takeMarkdownDraft, trackMarkdownComposition, type MarkdownModuleContext, type MarkdownModuleRuntime } from '../../core'
import { mathStrings } from './runtimeStrings'
import styles from './style.css?inline'
import { takeMathEditRequest } from './inputRules'
import { normalizeMath, type MarkdownMathData } from './index'

const loadKatex = createMarkdownModuleLoader(async () => (await import('katex')).default)
type Katex = Awaited<ReturnType<typeof loadKatex>>
type Context = MarkdownModuleContext<MarkdownMathData>
const katexOptions = (displayMode: boolean) => ({ displayMode, output: 'mathml' as const, trust: false,
  throwOnError: true, strict: 'error' as const, maxExpand: 1000, maxSize: 20 })
const messageOf = (cause: unknown) => cause instanceof Error ? cause.message : mathStrings.invalidExpression
/** The parse error of a formula as a sentence, or '' when it renders. */
function errorOf(katex: Katex, expression: string, displayMode: boolean) {
  try { katex.renderToString(expression, katexOptions(displayMode)); return '' } catch (cause) { return mathStrings.renderFailed(messageOf(cause)).trim() }
}

/**
 * The formula field shared by the block panel and the inline popover: a textarea writing every keystroke to the node (one typing step
 * of the history) with the parse error under it. Esc calls `close`; Ctrl+Z / Ctrl+Y go to the editor's history, not the textarea's.
 */
function sourceField(context: Context, displayMode: boolean, close: () => void) {
  const doc = context.container.ownerDocument
  const root = doc.createElement('div'); root.className = 'md-math-source'
  const label = doc.createElement('label'); label.textContent = mathStrings.expressionLabel
  const textarea = doc.createElement('textarea'); textarea.rows = 3; textarea.spellcheck = false; textarea.maxLength = 10_000
  textarea.autocapitalize = 'off'; textarea.autocomplete = 'off'; label.append(textarea)
  // What the field stands on: the node's data as it is now, to tell a draft the node does not hold yet.
  let base = context.data
  const kept = context.editor?.isEditable ? takeMarkdownDraft<string>(context.editor, 'math', base) : undefined
  textarea.value = kept ?? base.expression
  const error = doc.createElement('p'); error.className = 'md-math-error'; error.setAttribute('role', 'alert'); error.hidden = true
  const hint = doc.createElement('p'); hint.className = 'md-math-hint'; hint.textContent = mathStrings.editHint
  const done = doc.createElement('button'); done.type = 'button'; done.textContent = mathStrings.hideSource; done.onclick = close
  root.append(label, error, hint, done)
  const composition = trackMarkdownComposition(root)
  const show = (message: string) => { error.textContent = message; error.hidden = !message }
  const check = async () => { const text = textarea.value, message = errorOf(await loadKatex(), text, displayMode); if (text === textarea.value) show(message) }
  const write = () => {
    try { context.update?.(normalizeMath({ version: 1, expression: textarea.value }), { history: 'typing' }) } catch (cause) { show(messageOf(cause)); return }
    void check()
  }
  textarea.oninput = write
  textarea.onfocus = textarea.onblur = () => context.closeHistory?.()
  textarea.onkeydown = event => {
    if (composition.active() || isMarkdownComposition(event, context.editor)) return
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close(); return }
    if ((event.ctrlKey || event.metaKey) && ['z', 'y'].includes(event.key.toLowerCase())) {
      event.preventDefault(); event.stopPropagation()
      if (event.shiftKey || event.key.toLowerCase() === 'y') context.editor?.commands.redo(); else context.editor?.commands.undo()
    }
  }
  textarea.addEventListener('beforeinput', event => {
    if (event.isComposing || composition.active() || !['historyUndo', 'historyRedo'].includes(event.inputType)) return
    event.preventDefault(); event.stopPropagation()
    if (event.inputType === 'historyRedo') context.editor?.commands.redo(); else context.editor?.commands.undo()
  })
  // A draft kept when writing stopped is written as the typing step it was.
  if (kept !== undefined) write()
  else void check()
  return { root,
    /** The node changed (undo, a collaborator): the text follows unless it already shows it. The writer stays the one the field was opened
     *  with: the inline popover's edit context, whose writes keep the edit open (block.ts `editWriting`); the render's would close it. */
    sync(next: Context) { base = next.data; if (textarea.value !== next.data.expression) { textarea.value = next.data.expression; void check() } },
    focus(select = true) { textarea.focus(); if (select) textarea.select() },
    /** Writing stopped or came back while the field stays (contract J1.2): an unwritten formula is kept and announced, then dropped. */
    writable(on: boolean) {
      if (!context.editor) return
      if (on) takeMarkdownDraft(context.editor, 'math', base)
      else if (textarea.value !== base.expression) retainMarkdownDraft(context.editor, 'math', mathStrings.expressionLabel, base, textarea.value)
    },
    /** Writing stopped with a formula that was not written (it did not parse): kept for the next edit, announced. */
    destroy() { composition.destroy(); if (context.editor && textarea.value !== base.expression) retainMarkdownDraft(context.editor, 'math', mathStrings.expressionLabel, base, textarea.value) } }
}

function runtime(displayMode: boolean): MarkdownModuleRuntime<MarkdownMathData> {
  /** Per rendered formula: opens its field (block) and feeds the open field the node's new data. */
  const panels = new WeakMap<HTMLElement, (anchor?: HTMLElement) => void>()
  const fields = new WeakMap<HTMLElement, (next: Context) => void>()
  return {
    // A block formula is read: a click selects it, Enter or the toolbar's button opens its source. An inline one is a chip (its click opens).
    editOnClick: !displayMode,
    async render(initial) {
      let context = initial
      const { container, signal } = context, doc = container.ownerDocument
      const shown = doc.createElement(displayMode ? 'div' : 'span'); shown.className = 'md-math'
      const output = doc.createElement(displayMode ? 'div' : 'span'); output.textContent = context.data.expression
      const style = doc.createElement('style'); style.textContent = styles
      shown.append(output); container.append(style, shown)
      const katex = await loadKatex()
      if (signal.aborted) return
      let ready = false, editableChanged = (_on: boolean) => {}
      const paint = (expression: string) => { try {
        // Native MathML needs no global stylesheet/fonts and exposes accessible math semantics.
        // KaTeX empties its target before it throws, so the swap happens only once the new formula parsed.
        const next = doc.createElement('span'); katex.render(expression, next, katexOptions(displayMode))
        output.replaceChildren(...next.childNodes)
        ready = true
      } catch (cause) {
        // A formula that stops parsing while it is typed keeps its last good render; the field says why.
        if (ready) return
        const source = doc.createElement('code'); source.textContent = expression
        const error = doc.createElement('span'); error.setAttribute('role', 'alert')
        error.textContent = mathStrings.renderFailed(messageOf(cause))
        output.replaceChildren(source, error)
      } }
      paint(context.data.expression)
      // What a reader may do with a block formula too (J1.3): take its TeX. Shown at the formula's corner when it is near.
      if (displayMode) addMarkdownReadActions(shown, signal, [{ label: markdownStrings.modules.copyWhat('TeX'), icon: 'copy', run: button => copyMarkdownText(button, context.data.expression) }])
      // The panel (and its alert region) only where an editor can open it; a reader or the static renderer gets the formula alone.
      if (displayMode && context.update) {
        let anchor: HTMLElement | undefined
        // A click inside the open panel (its label, hint, error) is no click on the block.
        const panel = doc.createElement('div'); panel.className = 'md-math-panel'; panel.hidden = true; panel.dataset.mdNoSelect = ''
        // The anchor may be the block itself (not focusable): then the selected block keeps the keyboard in the editor.
        const close = () => { open(false); anchor?.focus(); if (doc.activeElement !== anchor) context.editor?.view.focus() }
        const field = sourceField(context, true, close)
        panel.append(field.root); shown.append(panel)
        // A press on the panel's text (label, hint, error) keeps the keyboard in the formula field, where Esc closes it.
        panel.addEventListener('mousedown', event => { if (!(event.target as Element).closest('textarea, button')) { event.preventDefault(); field.focus(false) } })
        // A press outside the formula (and its toolbar) closes the panel as on any other in-place edit (P4, D2): the text is already
        // saved, and the keyboard stays where the person went.
        const outside = (event: Event) => {
          const target = event.target as Node
          if (!shown.contains(target) && !context.toolbar?.element.contains(target)) open(false)
        }
        const open = (show: boolean) => {
          if (panel.hidden === show) context.closeHistory?.()
          panel.hidden = !show; anchor?.setAttribute('aria-expanded', String(show))
          if (show) { doc.addEventListener('pointerdown', outside, true); field.focus() } else doc.removeEventListener('pointerdown', outside, true)
        }
        const editable = () => !signal.aborted && !!context.editor?.isEditable && !!context.update
        editableChanged = on => { if (!on) open(false); field.writable(on) }
        panels.set(shown, next => { if (editable()) { anchor = next; open(panel.hidden === true) } })
        fields.set(shown, field.sync)
        signal.addEventListener('abort', () => { panels.delete(shown); fields.delete(shown); field.destroy(); doc.removeEventListener('pointerdown', outside, true) }, { once: true })
        if (!!context.editor && takeMathEditRequest(context.editor) && editable()) open(true)
      }
      return {
        update(next) {
          if (signal.aborted) return
          const changed = next.data.expression !== context.data.expression
          context = next
          if (changed) paint(next.data.expression)
          fields.get(shown)?.(next)
        },
        // The formula stays drawn; a read-only editor closes its source panel.
        editable: on => { if (!signal.aborted) editableChanged(on) },
        prepare: async () => ready && !signal.aborted, destroy() { panels.delete(shown); fields.delete(shown) },
      }
    },
    edit(context) {
      const shown = context.container.querySelector<HTMLElement>('.md-math')
      if (!shown) return
      if (displayMode) { panels.get(shown)?.(context.anchor); return }
      const { editor } = context
      if (!editor || !context.update || !context.anchor) return
      // Every keystroke rewrites the node, which ends its node selection: the popover follows its anchor, not the selection, and the
      // formula is selected again when it closes, so the keyboard returns to it (P7).
      const reselect = () => {
        const at = context.getPos?.()
        if (at !== undefined && !editor.isDestroyed && editor.state.doc.nodeAt(at)?.type.name === 'mathInline') editor.commands.setNodeSelection(at)
      }
      return openMarkdownContextPopover({ editor, anchor: context.anchor, title: mathStrings.editTitle, signal: context.signal, className: 'md-math-popover', trackSelection: false,
        render(popover, close) {
          const field = sourceField(context, false, close); popover.append(field.root); field.focus()
          fields.set(shown, field.sync)
          return () => { fields.delete(shown); field.destroy(); context.closeHistory?.(); reselect() }
        },
      })
    },
  }
}
export const mathBlockRuntime = runtime(true)
export const mathInlineRuntime = runtime(false)
