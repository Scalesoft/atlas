import type { Editor } from '@tiptap/core'
import { createIconElement, openMarkdownContextPopover, type MarkdownIconName } from '../../core'
import { chartsStrings } from './runtimeStrings'
import { normalizeChart, type ChartData, type ChartKind } from './data'
import styles from './style.css?inline'

export const chartKindLabels: Record<ChartKind, string> = {
  get bar() { return chartsStrings.kindBar }, get line() { return chartsStrings.kindLine }, get pie() { return chartsStrings.kindPie }, get doughnut() { return chartsStrings.kindDoughnut },
}
const hints: Record<ChartKind, string> = {
  get bar() { return chartsStrings.hintBar }, get line() { return chartsStrings.hintLine }, get pie() { return chartsStrings.hintPie }, get doughnut() { return chartsStrings.hintDoughnut },
}
const chartKindIcons: Record<ChartKind, MarkdownIconName> = {
  bar: 'bar-chart-3', line: 'line-chart', pie: 'pie-chart', doughnut: 'donut',
}

/** The picker and the toolbar name the shared registry, so a chart kind looks the same everywhere. */
export function createChartKindIcon(doc: Document, kind: ChartKind): SVGSVGElement {
  const svg = createIconElement(doc, chartKindIcons[kind], 16)
  svg.classList.add('md-chart-kind-icon')
  return svg
}

export function openChartTypePicker(context: { editor: Editor; anchor: HTMLElement; signal: AbortSignal; data: ChartData; update: (data: ChartData) => void }): () => void {
  const { editor, anchor, signal, data, update } = context
  return openMarkdownContextPopover({ editor, anchor, signal, title: chartsStrings.typeLabel, variant: 'menu', className: 'md-chart-type-popover', render(host, close) {
    const doc = host.ownerDocument
    const style = doc.createElement('style'); style.textContent = styles
    const choices = doc.createElement('div'); choices.className = 'md-chart-types'; choices.setAttribute('role', 'group'); choices.setAttribute('aria-label', chartsStrings.kindGroupAriaLabel)
    const error = doc.createElement('p'); error.setAttribute('role', 'alert'); error.className = 'md-chart-type-error'
    const buttons: HTMLButtonElement[] = []
    for (const kind of Object.keys(chartKindLabels) as ChartKind[]) {
      const button = doc.createElement('button'); button.type = 'button'; button.className = 'md-chart-type-card'
      button.setAttribute('aria-label', chartKindLabels[kind]); button.setAttribute('role', 'menuitemradio'); button.setAttribute('aria-checked', String(data.kind === kind))
      const preview = doc.createElement('span'); preview.className = 'md-chart-type-preview'; preview.append(createChartKindIcon(doc, kind))
      const label = doc.createElement('span'); label.className = 'md-chart-type-label'; label.textContent = chartKindLabels[kind]
      const hint = doc.createElement('span'); hint.className = 'md-chart-type-hint'; hint.textContent = hints[kind]
      const selected = createIconElement(doc, 'check', 16); selected.classList.add('md-chart-type-check')
      button.append(preview, label, hint, selected)
      button.onclick = () => {
        if (signal.aborted || editor.isDestroyed || !editor.isEditable) return
        if (kind === data.kind) { close(); return }
        try { update(normalizeChart({ ...data, kind })); close() }
        catch (cause) { error.textContent = (cause as Error).message }
      }
      button.onkeydown = event => {
        const index = buttons.indexOf(button)
        const next = ({ ArrowRight: (index + 1) % 4, ArrowLeft: (index + 3) % 4, ArrowDown: (index + 2) % 4, ArrowUp: (index + 2) % 4, Home: 0, End: 3 } as Record<string, number>)[event.key]
        if (next !== undefined) { event.preventDefault(); buttons[next]?.focus() }
      }
      buttons.push(button); choices.append(button)
    }
    host.append(style, choices, error)
  } })
}
