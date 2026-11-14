import { describe, expect, it } from 'vitest'
import { DAY, MIN_HISTORY_SPAN, historyEntryAt, historyMarks, historyTicks, historyWindow, historyZoomSpan, historyZoomValue } from './timeline'
import type { MarkdownHistoryEntry } from './types'

const entry = (id: string, time: number, kind: MarkdownHistoryEntry['kind'] = 'edit'): MarkdownHistoryEntry => ({ id, timestamp: new Date(time).toISOString(), author: 'Anna', label: 'Edit', kind })
describe('history timeline geometry and chronology', () => {
  it.each([-DAY, 0, DAY, 10 * DAY, 100 * DAY])('clamps the visible window at center %s', center => {
    const window = historyWindow(center, DAY, { from: 0, to: 10 * DAY })
    expect(window.from).toBeGreaterThanOrEqual(0); expect(window.to).toBeLessThanOrEqual(10 * DAY)
    expect(window.to - window.from).toBe(DAY)
  })
  it('renders a meaningful window for a single checkpoint', () => {
    expect(historyWindow(100, 0, { from: 100, to: 100 })).toEqual({ from: 100, to: 100 + MIN_HISTORY_SPAN })
  })
  it('clamps oversized and undersized zoom windows', () => {
    expect(historyWindow(5 * DAY, 100 * DAY, { from: 0, to: 10 * DAY })).toEqual({ from: 0, to: 10 * DAY })
    expect(historyWindow(DAY, 1, { from: 0, to: 10 * DAY }).to - historyWindow(DAY, 1, { from: 0, to: 10 * DAY }).from).toBe(MIN_HISTORY_SPAN)
  })
  it.each([0, 1, 10, 25, 50, 75, 99, 100])('round-trips logarithmic zoom %s', value => {
    expect(historyZoomValue(historyZoomSpan(value, 365 * DAY), 365 * DAY)).toBeCloseTo(value, 8)
  })
  it('handles zoom extremes and a zero-length history', () => {
    expect(historyZoomSpan(-5, DAY)).toBe(DAY)
    expect(historyZoomSpan(105, DAY)).toBeCloseTo(MIN_HISTORY_SPAN)
    expect(historyZoomSpan(50, 0)).toBe(MIN_HISTORY_SPAN)
    expect(historyZoomValue(0, 0)).toBe(0)
  })
  const entries = [entry('newest', 3000), entry('same-new', 2000), entry('same-old', 2000), entry('oldest', 1000)]
  it.each([[4000, 'newest'], [3000, 'newest'], [2999, 'same-new'], [2000, 'same-new'], [1999, 'oldest'], [0, 'oldest']])('selects the document at time %s', (time, id) => {
    expect(historyEntryAt(entries, Number(time))?.id).toBe(id)
  })
  it('handles an empty timeline', () => { expect(historyEntryAt([], 0)).toBeUndefined(); expect(historyMarks([], { from: 0, to: 10 }, 300)).toEqual([]) })
  it('prioritizes decisions when dense edits occupy the same pixel', () => {
    const marks = historyMarks([entry('a', 100, 'edit'), entry('b', 101, 'structure'), entry('c', 102, 'approve')], { from: 0, to: 10000 }, 300)
    expect(marks).toHaveLength(1); expect(marks[0]).toMatchObject({ count: 3, kind: 'approve' })
  })
  it('aggregates 100,000 edits into a bounded number of visual marks', () => {
    const many = Array.from({ length: 100000 }, (_, i) => entry(String(i), i))
    const marks = historyMarks(many, { from: 0, to: 100000 }, 736)
    expect(marks.length).toBeLessThanOrEqual(241)
    expect(marks.reduce((total, mark) => total + mark.count, 0)).toBe(100000)
  })
  it('keeps all marks inside the right edge and excludes other periods', () => {
    const marks = historyMarks([entry('before', -1), entry('first', 0), entry('last', 100), entry('after', 101)], { from: 0, to: 100 }, 300)
    expect(marks.reduce((n, mark) => n + mark.count, 0)).toBe(2)
    expect(marks.every(mark => mark.x >= 0 && mark.x <= 296)).toBe(true)
  })
  it.each([0, -1])('does not draw into a hidden or invalid surface (%s)', width => {
    expect(historyMarks(entries, { from: 0, to: 10000 }, width)).toEqual([])
    expect(historyTicks({ from: 0, to: DAY }, width)).toEqual([])
  })
  it.each([MIN_HISTORY_SPAN, DAY, 7 * DAY, 365 * DAY, 10000 * DAY])('keeps tick density bounded at span %s', span => {
    expect(historyTicks({ from: 0, to: span }, 300).length).toBeLessThanOrEqual(5)
    expect(historyTicks({ from: 0, to: span }, 736).length).toBeLessThanOrEqual(9)
  })
})
