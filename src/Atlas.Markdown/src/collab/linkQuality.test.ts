import { expect, it, vi } from 'vitest'
import { createLinkQuality } from './linkQuality'

function harness(stallAfterMs = 2000) {
  let clock = 0
  const timers: { at: number; run: () => void }[] = []
  const stalled = vi.fn()
  const quality = createLinkQuality(stalled, {
    stallAfterMs,
    now: () => clock,
    schedule: (run, ms) => {
      const timer = { at: clock + ms, run }
      timers.push(timer)
      return () => { const index = timers.indexOf(timer); if (index >= 0) timers.splice(index, 1) }
    },
  })
  return {
    quality, stalled,
    advance(ms: number) {
      clock += ms
      for (const timer of timers.filter(entry => entry.at <= clock)) {
        timers.splice(timers.indexOf(timer), 1)
        timer.run()
      }
    },
  }
}

it('keeps one frozen reading while nothing changes so the editor state does not churn', () => {
  const { quality } = harness()
  const first = quality.observe('connected', 0)
  expect(first).toEqual({ latencyMs: null, drops: 0, stalled: false })
  expect(quality.observe('connected', 0)).toBe(first)
  expect(Object.isFrozen(first)).toBe(true)
})

it('measures the acknowledgement round-trip of a local edit', () => {
  const { quality, advance } = harness()
  quality.observe('connected', 0)
  quality.observe('connected', 1)
  advance(120)
  expect(quality.observe('connected', 0).latencyMs).toBe(120)
})

it('reports the median of recent round-trips so one slow batch does not dominate', () => {
  const { quality, advance } = harness()
  quality.observe('connected', 0)
  for (const duration of [40, 60, 900, 50, 30]) {
    quality.observe('connected', 1)
    advance(duration)
    quality.observe('connected', 0)
  }
  expect(quality.observe('connected', 0).latencyMs).toBe(50)
})

it('flags a stall and announces it once the acknowledgement is overdue', () => {
  const { quality, stalled, advance } = harness(2000)
  quality.observe('connected', 0)
  quality.observe('connected', 1)
  advance(1999)
  expect(quality.observe('connected', 1).stalled).toBe(false)
  expect(stalled).not.toHaveBeenCalled()
  advance(1)
  expect(stalled).toHaveBeenCalledOnce()
  expect(quality.observe('connected', 1).stalled).toBe(true)
  quality.observe('connected', 0)
  expect(quality.observe('connected', 0).stalled).toBe(false)
})

it('counts a lost connection but not the first connect', () => {
  const { quality } = harness()
  expect(quality.observe('connecting', 0).drops).toBe(0)
  expect(quality.observe('connected', 0).drops).toBe(0)
  expect(quality.observe('reconnecting', 0).drops).toBe(1)
  expect(quality.observe('offline', 0).drops).toBe(1)
  expect(quality.observe('connected', 0).drops).toBe(1)
  expect(quality.observe('offline', 0).drops).toBe(2)
})

it('measures acknowledged edits during sustained typing without waiting for an empty queue', () => {
  const { quality, stalled, advance } = harness()
  quality.observe('connected', 1)
  for (let batch = 0; batch < 30; batch++) {
    advance(100)
    quality.observe('connected', 2) // The next edit arrives before the previous ACK.
    const reading = quality.observe('connected', 1)
    expect(reading.latencyMs).toBe(100)
    expect(reading.stalled).toBe(false)
  }
  expect(stalled).not.toHaveBeenCalled()
  advance(100)
  expect(quality.observe('connected', 0).latencyMs).toBe(100)
})

it('keeps the oldest remaining edit deadline when a batch is acknowledged', () => {
  const { quality, stalled, advance } = harness()
  quality.observe('connected', 2)
  advance(1000)
  quality.observe('connected', 3)
  advance(500)
  expect(quality.observe('connected', 1).latencyMs).toBe(1500)
  advance(1499)
  expect(stalled).not.toHaveBeenCalled()
  advance(1)
  expect(stalled).toHaveBeenCalledOnce()
  expect(quality.observe('connected', 1).stalled).toBe(true)
})

it('does not blame an outage for slow acknowledgements', () => {
  const { quality, stalled, advance } = harness()
  quality.observe('connected', 0)
  quality.observe('connected', 1)
  advance(5000)
  quality.observe('offline', 1)
  stalled.mockClear()
  advance(5000)
  expect(stalled).not.toHaveBeenCalled()
  quality.observe('connected', 1)
  advance(40)
  const reading = quality.observe('connected', 0)
  expect(reading.latencyMs).toBe(40)
  expect(reading.stalled).toBe(false)
})

it('stops measuring after it is destroyed', () => {
  const { quality, stalled, advance } = harness()
  quality.observe('connected', 1)
  quality.destroy()
  advance(10_000)
  expect(stalled).not.toHaveBeenCalled()
})
