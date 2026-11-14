import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SnapshotScheduler } from './snapshotScheduler'

function idleWindow() {
  const target = new EventTarget()
  const idle = new Map<number, IdleRequestCallback>()
  let ids = 0
  const window = Object.assign(target, {
    requestIdleCallback: vi.fn((callback: IdleRequestCallback) => { idle.set(++ids, callback); return ids }),
    cancelIdleCallback: vi.fn((id: number) => { idle.delete(id) }),
  })
  const runIdle = (remaining = 50) => { const due = [...idle.values()]; idle.clear(); due.forEach(callback => callback({ didTimeout: false, timeRemaining: () => remaining })) }
  return { window, idle, runIdle, key: () => target.dispatchEvent(new Event('keydown')) }
}

describe('SnapshotScheduler', () => {
  beforeEach(() => { vi.useFakeTimers() })
  afterEach(() => { vi.useRealTimers() })

  it('runs a due snapshot in idle time once keys and pointer rest', () => {
    const push = vi.fn()
    const { window, idle, runIdle, key } = idleWindow()
    const scheduler = new SnapshotScheduler(push, window as never)
    scheduler.schedule()
    vi.advanceTimersByTime(1000)
    expect(push).not.toHaveBeenCalled()
    expect(idle.size).toBe(1)

    // A key while waiting for idle time: the snapshot waits until the user rests again.
    key()
    runIdle()
    expect(push).not.toHaveBeenCalled()
    vi.advanceTimersByTime(800)
    runIdle()
    expect(push).toHaveBeenCalledOnce()
    scheduler.destroy()
  })

  it('waits past short gaps between other tasks for a free thread', () => {
    const push = vi.fn()
    const { window, idle, runIdle } = idleWindow()
    const scheduler = new SnapshotScheduler(push, window as never)
    scheduler.schedule()
    vi.advanceTimersByTime(1000)
    runIdle(6)
    expect(push).not.toHaveBeenCalled()
    expect(idle.size).toBe(1)
    runIdle(45)
    expect(push).toHaveBeenCalledOnce()
    scheduler.destroy()
  })

  it('does not let continuous typing postpone a snapshot past another user\'s save window (about 6 s, savePage.ts)', () => {
    const push = vi.fn()
    const { window, key } = idleWindow()
    const scheduler = new SnapshotScheduler(push, window as never)
    let elapsed = 0
    // Every key is an edit too: the debounce never settles, the snapshot is due at its max wait (2 s) and deferred 2,5 s at most.
    for (; elapsed < 15_000 && push.mock.calls.length === 0; elapsed += 200) {
      key()
      scheduler.schedule()
      vi.advanceTimersByTime(200)
    }
    expect(push).toHaveBeenCalledOnce()
    expect(elapsed).toBeGreaterThan(2_500)
    expect(elapsed).toBeLessThanOrEqual(2_000 + 2_500 + 200)
    expect(elapsed).toBeLessThan(6_000)
    scheduler.destroy()
  })

  it('runs it right after a key is drawn in the last half second before the deadline, so it never lands on the next key', () => {
    const push = vi.fn()
    const { window, key } = idleWindow()
    const frames: FrameRequestCallback[] = []
    Object.assign(window, { requestAnimationFrame: (callback: FrameRequestCallback) => frames.push(callback) })
    const scheduler = new SnapshotScheduler(push, window as never)
    let elapsed = 0
    // Keys every 300 ms: due at 2 s, the deadline 2,5 s later; from 0,5 s before it the next key's frame runs the snapshot.
    for (; elapsed < 6_000 && push.mock.calls.length === 0; elapsed += 300) {
      key()
      scheduler.schedule()
      frames.splice(0).forEach(frame => frame(0))
      vi.advanceTimersByTime(1)
      if (push.mock.calls.length) break
      vi.advanceTimersByTime(299)
    }
    expect(push).toHaveBeenCalledOnce()
    expect(elapsed).toBeGreaterThanOrEqual(2_000 + 2_500 - 500)
    expect(elapsed).toBeLessThan(2_000 + 2_500)
    scheduler.destroy()
  })

  it('flushes a scheduled or waiting snapshot at once and nothing when none waits', () => {
    const push = vi.fn()
    const { window, idle } = idleWindow()
    const scheduler = new SnapshotScheduler(push, window as never)
    scheduler.flush()
    expect(push).not.toHaveBeenCalled()

    scheduler.schedule()
    scheduler.flush()
    expect(push).toHaveBeenCalledOnce()
    vi.advanceTimersByTime(5000)
    expect(push).toHaveBeenCalledOnce()

    scheduler.schedule()
    vi.advanceTimersByTime(1000)
    expect(idle.size).toBe(1)
    scheduler.flush()
    expect(push).toHaveBeenCalledTimes(2)
    expect(idle.size).toBe(0)
    scheduler.destroy()
  })

  it('runs without idle callbacks once the user rests, and stops listening on destroy', () => {
    const push = vi.fn()
    const target = new EventTarget()
    const remove = vi.spyOn(target, 'removeEventListener')
    const scheduler = new SnapshotScheduler(push, target as never)
    scheduler.schedule()
    vi.advanceTimersByTime(1000)
    expect(push).toHaveBeenCalledOnce()
    scheduler.destroy()
    expect(remove).toHaveBeenCalledWith('keydown', expect.any(Function), { capture: true })
    expect(remove).toHaveBeenCalledWith('pointerdown', expect.any(Function), { capture: true })
  })
})
