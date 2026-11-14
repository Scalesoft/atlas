import { addMarkdownReadActions, copyMarkdownText, createMarkdownModuleLoader, markdownStrings, zoomMarkdownBlock, openMarkdownContextPopover, retainMarkdownDraft, takeMarkdownDraft, type MarkdownModuleContext, type MarkdownModuleRuntime } from '../../core'
import { chartsStrings } from './runtimeStrings'
import { defaultChart, normalizeChart, type ChartData } from './data'
import { focusChartSource, selectedChartSource } from './source'
import { chartKindLabels, createChartKindIcon, openChartTypePicker } from './typePicker'
import type { createChartRenderer } from './chartRenderer'
import styles from './style.css?inline'

const loadRenderer = createMarkdownModuleLoader(() => import('./chartRenderer'))
export const chartRuntime: MarkdownModuleRuntime<ChartData> = {
  // The plot is read (tooltips, a reader's zoom): a click selects the chart, Enter or the toolbar's button opens its data.
  editOnClick: false,
  render(initial) {
    let context = initial
    const { container, signal, toolbar } = context
    const doc = container.ownerDocument
    const style = doc.createElement('style'); style.textContent = styles; container.append(style)
    const title = doc.createElement('h3'); title.className = 'md-chart-title'
    const visual = doc.createElement('div'); visual.className = 'md-chart-visual'; visual.setAttribute('role', 'img')
    const status = doc.createElement('p'); status.setAttribute('role', 'status'); status.className = 'md-chart-status'
    const details = doc.createElement('details'), summary = doc.createElement('summary'); summary.textContent = chartsStrings.dataCaption; details.append(summary)
    const table = doc.createElement('table'); table.className = 'md-chart-data'; details.append(table)
    const head = doc.createElement('div'); head.className = 'md-chart-head'; head.append(title)
    // The data disclosure and the read actions share a line: the actions stay outside <details>, whose closed content is not rendered.
    const foot = doc.createElement('div'); foot.className = 'md-chart-foot'; foot.append(details)
    container.append(head, visual, status, foot)
    // What a reader may do with the chart too (J1.3): enlarge it and take its data, in the line of its data disclosure.
    const zoom = (opener: HTMLElement) => zoomMarkdownBlock({ opener, editor: context.editor, signal, label: context.data.title || chartsStrings.dataCaption,
        render(body) {
          const host = doc.createElement('div'); host.className = 'md-chart-visual'; host.setAttribute('role', 'img'); host.setAttribute('aria-label', visual.getAttribute('aria-label') ?? ''); body.append(host)
          let big: ReturnType<typeof createChartRenderer> | undefined
          void loadRenderer().then(runtime => { if (host.isConnected) big = runtime.createChartRenderer(host, context.data) }).catch(() => {})
          return () => big?.destroy()
        } })
    visual.addEventListener('dblclick', () => zoom(visual.closest<HTMLElement>('[data-md-module]') ?? visual))
    addMarkdownReadActions(foot, signal, [
      { label: markdownStrings.modules.zoomBlock, icon: 'maximize-2', run: zoom },
      { label: markdownStrings.modules.copyWhat(chartsStrings.dataCaption.toLowerCase()), icon: 'copy', run: button => copyMarkdownText(button, context.data.grid.map(row => row.join('\t')).join('\n')) },
    ])
    let stopped = false, loading = false, failed = false
    let renderer: ReturnType<typeof createChartRenderer> | undefined
    let preparation: Promise<boolean> = Promise.resolve(false)
    let closeType: (() => void) | undefined, closeEdit: (() => void) | undefined
    let typeButton: HTMLButtonElement | undefined, chip: HTMLButtonElement | undefined
    const canEdit = () => !!(context.editor && context.update && context.editor.isEditable && !signal.aborted)
    const openType = (anchor: HTMLElement) => {
      closeType?.()
      closeType = openChartTypePicker({ editor: context.editor!, anchor, signal, data: context.data, update: context.update! })
    }
    const openData = (anchor?: HTMLElement) => { if (!canEdit()) return; closeEdit?.(); closeEdit = openChartEditor(anchor ? { ...context, anchor } : context) }
    let endTitle: (() => void) | undefined
    // The edit affordances follow editability in place (contract J1.2): a flip rebuilds neither the chart nor its renderer.
    const setEditable = (on: boolean) => {
      if (!context.editor || !context.update || on === !!chip) return
      container.toggleAttribute('data-editable', on)
      if (!on) {
        endTitle?.(); closeType?.(); closeEdit?.()
        typeButton?.remove(); chip?.remove(); typeButton = chip = undefined
        for (const name of ['tabindex', 'title', 'aria-label']) title.removeAttribute(name)
        title.onclick = title.onkeydown = table.onclick = null; table.removeAttribute('title')
        return
      }
      if (toolbar) typeButton = toolbar.addButton({ label: chartsStrings.typeLabel, onClick: openType })
      // The type is a chip on the chart; the title and the data table edit where they are shown.
      chip = doc.createElement('button'); chip.type = 'button'; chip.className = 'md-chart-kind-chip'
      chip.onclick = event => { event.preventDefault(); if (canEdit()) openType(chip!) }
      head.append(chip)
      title.tabIndex = 0; title.title = chartsStrings.titleEditLabel; title.setAttribute('aria-label', chartsStrings.titleEditLabel)
      const editTitle = () => {
        if (!canEdit() || title.querySelector('input')) return
        const input = doc.createElement('input'); input.className = 'md-chart-title-input'; input.maxLength = 300
        input.value = takeMarkdownDraft<string>(context.editor!, 'chart-title', context.data) ?? context.data.title; input.setAttribute('aria-label', chartsStrings.titleFieldLabel)
        let done = false
        // The right went with the field open: the render is replaced, so the text is kept for the next edit and announced.
        signal.addEventListener('abort', () => { if (!done && input.value !== context.data.title) retainMarkdownDraft(context.editor!, 'chart-title', chartsStrings.titleFieldLabel, context.data, input.value) }, { once: true })
        // A blur (a click or Tab elsewhere) leaves the keyboard where the person went; Enter and Esc keep it on the title.
        const finish = (save: boolean, refocus = true) => {
          if (done) return
          done = true
          const value = input.value
          title.textContent = context.data.title
          if (save && value !== context.data.title && canEdit()) { try { context.update!(normalizeChart({ ...context.data, title: value })) } catch { /* invalid title: keep the old one */ } }
          if (refocus && (doc.activeElement === doc.body || !doc.activeElement)) title.focus()
        }
        input.onkeydown = event => {
          event.stopPropagation()
          if (event.key === 'Enter') { event.preventDefault(); finish(true) } else if (event.key === 'Escape') { event.preventDefault(); finish(false) }
        }
        input.onblur = () => finish(true, false)
        // Writing stopped with the field open: the text is kept for the next edit, as when the render is replaced.
        endTitle = () => { if (!done && input.value !== context.data.title) retainMarkdownDraft(context.editor!, 'chart-title', chartsStrings.titleFieldLabel, context.data, input.value); finish(false, false) }
        title.replaceChildren(input); input.focus(); input.select()
      }
      title.onclick = event => { if (event.target === title) { event.preventDefault(); editTitle() } }
      title.onkeydown = event => { if (event.target === title && event.key === 'Enter') { event.preventDefault(); event.stopPropagation(); editTitle() } }
      table.title = chartsStrings.dataTableEditTitle
      table.onclick = event => { event.preventDefault(); openData(table) }
    }
    let signature = ''
    const refreshContent = () => {
      const data = context.data
      if (!title.querySelector('input')) title.textContent = data.title
      visual.setAttribute('aria-label', chartsStrings.visualAriaLabel(chartKindLabels[data.kind], data.title))
      status.textContent = failed ? chartsStrings.renderFailed
        : data.sourceStatus === 'linked' ? chartsStrings.linked
        : data.sourceStatus === 'missing' ? chartsStrings.sourceMissing
        : data.sourceStatus === 'invalid' ? chartsStrings.sourceInvalid : ''
      for (const control of [typeButton, chip]) {
        if (!control) continue
        const label = doc.createElement('span'); label.textContent = chartKindLabels[data.kind]
        control.replaceChildren(createChartKindIcon(doc, data.kind), label)
      }
      chip?.setAttribute('aria-label', chartsStrings.typeChipLabel(chartKindLabels[data.kind]))
      const nextSignature = JSON.stringify([data.title, data.grid])
      if (signature === nextSignature) return
      signature = nextSignature
      const caption = doc.createElement('caption'); caption.textContent = data.title || chartsStrings.dataCaption
      const head = doc.createElement('thead'), body = doc.createElement('tbody')
      data.grid.forEach((row, r) => {
        const tr = doc.createElement('tr')
        row.forEach((value, c) => {
          const cell = doc.createElement(r === 0 || c === 0 ? 'th' : 'td'); cell.textContent = value || '—'
          if (r === 0 || c === 0) (cell as HTMLTableCellElement).scope = r === 0 ? 'col' : 'row'
          tr.append(cell)
        })
        ;(r === 0 ? head : body).append(tr)
      })
      table.replaceChildren(caption, head, body)
    }
    const retry = doc.createElement('button'); retry.type = 'button'; retry.textContent = chartsStrings.retryRender
    const render = async (): Promise<boolean> => {
      if (loading || stopped || signal.aborted) return false
      loading = true; retry.remove()
      try {
        const runtime = await loadRenderer()
        if (signal.aborted || stopped) return false
        renderer = runtime.createChartRenderer(visual, context.data)
        failed = false; refreshContent()
        return renderer.prepare ? await renderer.prepare() : true
      } catch {
        if (signal.aborted || stopped) return false
        failed = true; refreshContent(); details.open = true; container.append(retry)
        return false
      } finally { loading = false }
    }
    retry.onclick = () => { if (!loading) preparation = render() }
    setEditable(!!context.editor?.isEditable)
    refreshContent(); preparation = render()
    return {
      update(next) {
        if (stopped || signal.aborted) return
        const changed = JSON.stringify(context.data) !== JSON.stringify(next.data)
        if (changed) closeType?.()
        context = next
        refreshContent()
        if (changed) renderer?.update(context.data)
      },
      editable(on) { if (!stopped && !signal.aborted) { setEditable(on); refreshContent() } },
      async prepare() {
        while (!stopped && !signal.aborted) {
          const current = preparation
          const ready = await current
          if (current !== preparation) continue
          return !failed && !stopped && !signal.aborted && (renderer?.prepare ? await renderer.prepare() : ready)
        }
        return false
      },
      destroy() { if (stopped) return; stopped = true; closeType?.(); closeEdit?.(); renderer?.destroy() },
    }
  },
  edit: openChartEditor,
}
/**
 * Compact anchored inspector; cell changes remain a local draft until Použít (Ctrl+Enter), Esc or Zrušit throw it away. Leaving the
 * panel applies a valid draft (P4, D3: a press outside it or the focus going elsewhere); an invalid one keeps the panel open with its error.
 */
export function openChartEditor(context: MarkdownModuleContext<ChartData>): () => void {
  const { editor, update, signal } = context
  if (!editor || !update) return () => {}
  const doc = editor.view.dom.ownerDocument
  const anchor = context.anchor ?? context.container
  // Registered before the popover's own press handler (which closes it): the draft applies first. An invalid draft keeps the panel
  // (D3): its press counts as the panel's own for the popover, and still reaches whatever was pressed.
  // shortcut: a per-panel stand-in for the shared D3 veto openMarkdownContextPopover lacks; move there once it has one.
  const kept: HTMLElement[] = []
  let leave: ((event: Event) => void) | undefined
  const outside = (event: Event) => { kept.length = 0; leave?.(event) }
  doc.addEventListener('pointerdown', outside, true)
  const stop = () => doc.removeEventListener('pointerdown', outside, true)
  // The panel follows the chart, not the selection: a click into the page's text with an invalid draft must not close it.
  const close = openMarkdownContextPopover({ editor, anchor, title: chartsStrings.editTitle, signal, className: 'md-chart-inspector', trackSelection: false, interactionRoots: kept, render(dialog, close) {
  const style = doc.createElement('style'); style.textContent = styles; dialog.append(style)
  const form = doc.createElement('form')
  const titleLabel = doc.createElement('label'); titleLabel.textContent = chartsStrings.titleFieldLabel
  // What the panel was opened on, and a draft kept when writing stopped under the last one.
  // The chart as the page holds it now: a panel opened from the render holds a copy of the data it was rendered with.
  const current = (): ChartData => {
    const at = context.getPos?.(), node = at === undefined ? undefined : editor.state.doc.nodeAt(at)
    return node?.type.name === 'chart' ? node.attrs.data as ChartData : context.data
  }
  const opened = current()
  let seen = JSON.stringify(opened)
  const restored = takeMarkdownDraft<ChartData>(editor, 'chart', opened)
  const title = doc.createElement('input'); title.value = restored?.title ?? context.data.title; title.maxLength = 300; titleLabel.append(title)
  const help = doc.createElement('p'); help.className = 'md-chart-help'; help.textContent = context.data.source ? chartsStrings.linkedHelp : chartsStrings.manualHelp
  let draft = normalizeChart(restored ?? context.data)
  const gridHost = doc.createElement('div'); gridHost.className = 'md-chart-grid-scroll'
  const buttons = doc.createElement('div'); buttons.className = 'md-module-actions'
  const editSource = button(chartsStrings.editSourceButton)
  const addRow = button(chartsStrings.addRowButton), addColumn = button(chartsStrings.addColumnButton), detach = button(chartsStrings.detachButton), link = button(chartsStrings.useSelectionButton)
  addRow.setAttribute('aria-label', chartsStrings.addRowAriaLabel); addColumn.setAttribute('aria-label', chartsStrings.addColumnAriaLabel)
  const selected = selectedChartSource(editor.state.tr)
  link.hidden = !selected
  link.title = selected ? chartsStrings.useSelectionReadyTitle : chartsStrings.useSelectionEmptyTitle
  buttons.append(editSource, addRow, addColumn, detach, link)
  const error = doc.createElement('p'); error.setAttribute('role', 'alert')
  const actions = doc.createElement('div'); actions.className = 'md-module-actions'; const cancel = button(markdownStrings.cancel), save = button(markdownStrings.apply); save.type = 'submit'; save.className = 'md-context-primary'; actions.append(cancel, save)
  form.append(titleLabel, help, gridHost, buttons, error, actions); dialog.append(form)

  function button(text: string) { const element = doc.createElement('button'); element.type = 'button'; element.textContent = text; return element }
  const renderGrid = () => {
    const table = doc.createElement('table'); table.className = 'md-chart-data'
    const caption = doc.createElement('caption'); caption.textContent = draft.source ? chartsStrings.linkedDataCaption : chartsStrings.editDataCaption; table.append(caption)
    const tbody = doc.createElement('tbody')
    draft.grid.forEach((row, r) => {
      const tr = doc.createElement('tr')
      row.forEach((value, c) => {
        const cell = doc.createElement(r === 0 ? 'th' : 'td'), input = doc.createElement('input')
        if (r === 0 && c === 0 && !draft.source) input.autofocus = true
        input.value = value; input.maxLength = 2000; input.disabled = !!draft.source
        input.setAttribute('aria-label', r === 0 ? c === 0 ? chartsStrings.categoryNameAriaLabel : chartsStrings.seriesNameAriaLabel(c) : c === 0 ? chartsStrings.rowLabelAriaLabel(r) : chartsStrings.cellAriaLabel(r, c))
        if (r > 0 && c > 0) input.inputMode = 'decimal'
        input.oninput = () => { draft.grid[r][c] = input.value }; cell.append(input)
        if (r === 0 && c > 0 && draft.grid[0].length > 2 && !draft.source) { const remove = button('×'); remove.setAttribute('aria-label', chartsStrings.removeSeriesLabel(c)); remove.title = chartsStrings.removeSeriesLabel(c); remove.className = 'md-chart-remove'; remove.onclick = () => { draft.grid.forEach(values => values.splice(c, 1)); renderGrid() }; cell.append(remove) }
        tr.append(cell)
      })
      if (r > 0 && draft.grid.length > 2 && !draft.source) { const cell = doc.createElement('td'), remove = button('×'); remove.setAttribute('aria-label', chartsStrings.removeRowLabel(r)); remove.title = chartsStrings.removeRowLabel(r); remove.className = 'md-chart-remove'; remove.onclick = () => { draft.grid.splice(r, 1); renderGrid() }; cell.append(remove); tr.append(cell) }
      tbody.append(tr)
    })
    table.append(tbody); gridHost.replaceChildren(table)
    addRow.disabled = !!draft.source || draft.grid.length >= 201; addColumn.disabled = !!draft.source || draft.grid[0].length >= 21; detach.hidden = !draft.source; editSource.hidden = !draft.source
  }
  addRow.onclick = () => { draft.grid.push(Array.from({ length: draft.grid[0].length }, () => '')); renderGrid() }
  addColumn.onclick = () => { draft.grid.forEach((row, r) => row.push(r === 0 ? chartsStrings.newSeriesName(row.length) : '')); renderGrid() }
  editSource.onclick = () => { if (draft.source && focusChartSource(editor, draft.source)) close() }
  detach.onclick = () => { draft = { ...draft, source: null, sourceStatus: null }; renderGrid() }
  link.onclick = () => { if (!selected) return; try { draft = normalizeChart({ ...defaultChart, ...draft, grid: selected.grid, source: selected.source, sourceStatus: 'linked' }); error.textContent = ''; renderGrid() } catch (cause) { error.textContent = (cause as Error).message } }
  cancel.onclick = close
  form.onsubmit = event => { event.preventDefault(); commit() }
  // Ctrl+Enter applies from any cell without leaving the keyboard.
  form.addEventListener('keydown', event => { if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) { event.preventDefault(); commit() } })
  // Writes a changed draft as one undo step and selects the chart again, so the keyboard returns to it (P7); false when it is invalid.
  const apply = () => {
    // Someone else changed the chart while the panel was open: the first confirmation shows it instead of writing over their change (rule 6).
    if (JSON.stringify(current()) !== seen) { seen = JSON.stringify(current()); error.textContent = chartsStrings.changedMeanwhile; return false }
    try {
      const next = normalizeChart({ ...draft, title: title.value })
      if (!signal.aborted && !editor.isDestroyed && editor.isEditable && JSON.stringify(next) !== JSON.stringify(current())) {
        update(next); seen = JSON.stringify(next)
        const at = context.getPos?.()
        if (at !== undefined && editor.state.doc.nodeAt(at)) editor.commands.setNodeSelection(at)
      }
      return true
    } catch (cause) { error.textContent = (cause as Error).message; return false }
  }
  const commit = () => { if (apply()) close() }
  const panel = dialog.closest<HTMLElement>('.md-context-popover') ?? dialog
  const away = (target: EventTarget | null) => !!target && !panel.contains(target as Node) && !anchor.contains(target as Node)
  leave = event => { if (away(event.target) && !apply()) kept.push(event.target as HTMLElement) }
  form.addEventListener('focusout', event => { if (away(event.relatedTarget) && form.isConnected) apply() })
  renderGrid()
  ;(draft.source ? editSource : gridHost.querySelector<HTMLInputElement>('input'))?.focus()
  // Writing stopped with the panel open: the draft is kept for the next edit, announced, and nothing is written.
  return () => {
    stop()
    const unsaved = { ...draft, title: title.value }
    if (JSON.stringify(unsaved) !== JSON.stringify(opened)) retainMarkdownDraft(editor, 'chart', chartsStrings.editTitle, opened, unsaved)
  }
  } })
  if (!leave) stop()
  return close
}
