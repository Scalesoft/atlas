import type { MarkdownHistoryEntry, MarkdownHistoryKind } from './types'

export interface HistoryWindow { from: number; to: number }
export interface HistoryMark { x: number; count: number; kind: MarkdownHistoryKind }
export const MIN_HISTORY_SPAN = 60_000
export const DAY = 86_400_000

export function historyWindow(center: number, span: number, bounds: HistoryWindow): HistoryWindow {
  const size = Math.min(Math.max(MIN_HISTORY_SPAN, span), Math.max(MIN_HISTORY_SPAN, bounds.to - bounds.from))
  const lower = bounds.from, upper = Math.max(bounds.to, lower + size)
  const from = Math.max(lower, Math.min(center - size / 2, upper - size))
  return { from, to: from + size }
}

/** Logarithmic zoom: half the slider travel has the same relative precision at any scale. */
export function historyZoomSpan(value: number, max: number): number {
  const upper = Math.max(MIN_HISTORY_SPAN, max)
  return upper * Math.pow(MIN_HISTORY_SPAN / upper, Math.max(0, Math.min(100, value)) / 100)
}

export function historyZoomValue(span: number, max: number): number {
  if (max <= MIN_HISTORY_SPAN) return 0
  return Math.max(0, Math.min(100, 100 * Math.log(max / Math.max(MIN_HISTORY_SPAN, span)) / Math.log(max / MIN_HISTORY_SPAN)))
}

/** Entries are newest first. Equal timestamps resolve to the newer checkpoint. */
export function historyEntryAt(entries: readonly MarkdownHistoryEntry[], at: number): MarkdownHistoryEntry | undefined {
  let lo = 0, hi = entries.length
  while (lo < hi) {
    const mid = Math.floor((lo + hi) / 2)
    if (Date.parse(entries[mid]!.timestamp) > at) lo = mid + 1
    else hi = mid
  }
  return entries[lo] ?? entries.at(-1)
}

const priority: Record<MarkdownHistoryKind, number> = { edit: 0, create: 1, submit: 2, structure: 3, reject: 4, approve: 5 }

/** At most one mark per physical bucket, regardless of the number of stored edits. */
export function historyMarks(entries: readonly MarkdownHistoryEntry[], window: HistoryWindow, width: number): HistoryMark[] {
  if (width <= 0 || window.to <= window.from) return []
  const buckets = new Map<number, HistoryMark>()
  const pixel = Math.max(3, width / 240)
  for (const entry of entries) {
    const time = Date.parse(entry.timestamp)
    if (time < window.from || time > window.to) continue
    const x = Math.min(Math.max(0, width - 4), Math.floor((time - window.from) / (window.to - window.from) * width / pixel) * pixel)
    const mark = buckets.get(x)
    if (mark) { mark.count++; if (priority[entry.kind] > priority[mark.kind]) mark.kind = entry.kind }
    else buckets.set(x, { x, count: 1, kind: entry.kind })
  }
  return [...buckets.values()]
}

export function historyTicks(window: HistoryWindow, width: number): number[] {
  if (width <= 0) return []
  const count = Math.max(2, Math.floor(width / 95))
  const steps = [60_000, 300_000, 900_000, 1_800_000, 3_600_000, 10_800_000, 21_600_000, 43_200_000, DAY, 2 * DAY, 7 * DAY, 30 * DAY, 90 * DAY, 365 * DAY]
  const span = window.to - window.from
  const step = steps.find(candidate => span / candidate <= count) ?? Math.ceil(span / count)
  const result: number[] = []
  for (let time = Math.ceil(window.from / step) * step; time <= window.to && result.length <= count + 1; time += step) result.push(time)
  return result
}
