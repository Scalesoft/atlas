// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { joinMotion, type MotionClient } from './motionScheduler'

interface Frame { name: string; now: number; elapsed: number; restyle: boolean; detail: number }
let frames: Frame[] = []
const clients: MotionClient[] = []
function join(name: string, options: { doc?: Document; reduced?: boolean; cost?: number } = {}) {
  const client = joinMotion(options.doc ?? document, (now, elapsed, restyle, detail) => {
    frames.push({ name, now, elapsed, restyle, detail })
    // Simulated drawing work for the frame budget.
    if (options.cost) vi.setSystemTime(Date.now() + options.cost)
  }, () => options.reduced ?? false)
  clients.push(client)
  return client
}
function gaps(name = 'a') {
  const times = frames.filter(frame => frame.name === name).map(frame => frame.now)
  return times.slice(1).map((time, i) => time - times[i]!)
}
function fire(type: string, target: EventTarget = document) { target.dispatchEvent(new Event(type, { bubbles: true })) }

describe('motion scheduler', () => {
  beforeEach(() => {
    vi.useFakeTimers(); frames = []
    // Drawing time is measured with performance.now; tests simulate it by moving the system time.
    vi.spyOn(performance, 'now').mockImplementation(() => Date.now())
  })
  afterEach(() => { clients.splice(0).forEach(client => client.remove()); vi.useRealTimers(); vi.restoreAllMocks() })

  it('draws woken clients every frame for ten seconds, then eases to thirty frames per second', () => {
    join('a').wake()
    vi.advanceTimersByTime(9900)
    expect(Math.max(...gaps())).toBeLessThanOrEqual(17)
    frames = []; vi.advanceTimersByTime(5000)
    // Settling mixes both rates rather than switching at once.
    expect(new Set(gaps())).toEqual(new Set([16, 32]))
    vi.advanceTimersByTime(200); frames = []; vi.advanceTimersByTime(2000)
    expect(Math.min(...gaps())).toBeGreaterThanOrEqual(29)
    expect(Math.max(...gaps())).toBeLessThan(40)
    // Drift follows real time: an idle frame reports its whole gap.
    expect(frames.at(-1)!.elapsed).toBeGreaterThanOrEqual(29)
  })
  it('never draws reduced motion faster than its calm rate', () => {
    join('a', { reduced: true }).wake()
    vi.advanceTimersByTime(1000)
    expect(Math.min(...gaps())).toBeGreaterThanOrEqual(29)
    expect(Math.max(...gaps())).toBeLessThan(40)
  })
  it('extends the active window on every wake and draws nothing once stopped', () => {
    const client = join('a'); client.wake()
    for (let i = 0; i < 4; i++) { vi.advanceTimersByTime(9000); client.wake() }
    expect(Math.max(...gaps())).toBeLessThanOrEqual(17)
    client.stop(); frames = []
    vi.advanceTimersByTime(1000)
    expect(frames).toHaveLength(0)
  })
  it('freezes every client from dragstart until dragend, drop or the next pointer move', () => {
    join('a').wake(); join('b').wake()
    vi.advanceTimersByTime(200)
    for (const end of ['dragend', 'drop', 'pointermove']) {
      fire('dragstart'); frames = []
      vi.advanceTimersByTime(1000)
      expect(frames).toHaveLength(0)
      fire(end); vi.advanceTimersByTime(200)
      expect(new Set(frames.map(frame => frame.name))).toEqual(new Set(['a', 'b']))
    }
  })
  it('holds the ink still while typing, without requesting frames, and resumes afterwards', () => {
    const client = join('a'); client.wake()
    vi.advanceTimersByTime(100)
    frames = []; const request = vi.spyOn(window, 'requestAnimationFrame')
    for (let i = 0; i < 10; i++) { fire(i % 2 ? 'input' : 'beforeinput'); client.wake(); vi.advanceTimersByTime(200) }
    expect(frames).toHaveLength(0)
    expect(request.mock.calls.length).toBeLessThan(10)
    vi.advanceTimersByTime(800); frames = []; client.wake(); vi.advanceTimersByTime(300)
    expect(Math.max(...gaps())).toBeLessThanOrEqual(17)
  })
  it('treats a key in editable content as typing, but not keys on other elements', () => {
    const editable = document.createElement('div'); Object.defineProperty(editable, 'isContentEditable', { value: true })
    const plain = document.createElement('div'); document.body.append(editable, plain)
    join('a').wake(); vi.advanceTimersByTime(100)
    frames = []; fire('keydown', plain); vi.advanceTimersByTime(200)
    expect(frames.length).toBeGreaterThan(0)
    frames = []; fire('keydown', editable); vi.advanceTimersByTime(700)
    expect(frames).toHaveLength(0)
    editable.remove(); plain.remove()
  })
  it('sleeps on a timer while nothing is due instead of requesting every frame', () => {
    join('a').wake(); vi.advanceTimersByTime(16000)
    const request = vi.spyOn(window, 'requestAnimationFrame'); frames = []
    vi.advanceTimersByTime(1000)
    // About thirty idle draws, each preceded by at most a couple of frame requests.
    expect(frames.length).toBeGreaterThanOrEqual(28)
    expect(request.mock.calls.length).toBeLessThanOrEqual(frames.length * 2 + 1)
  })
  it('asks for style reads on the first frame, when turning active again, and every five seconds', () => {
    const client = join('a'); client.wake()
    vi.advanceTimersByTime(3000)
    expect(frames.filter(frame => frame.restyle).map(frame => frame.now)).toEqual([frames[0]!.now])
    vi.advanceTimersByTime(8000)
    const woken = frames.length; client.wake(); vi.advanceTimersByTime(50)
    expect(frames[woken]!.restyle).toBe(true)
    frames = []; vi.advanceTimersByTime(12000)
    expect(frames.filter(frame => frame.restyle)).toHaveLength(2)
  })
  it('starts sparse, adds specks while drawing is cheap and thins them only when it becomes costly', () => {
    const cost = { ms: 0 }
    const client = joinMotion(document, (now, elapsed, restyle, detail) => {
      frames.push({ name: 'a', now, elapsed, restyle, detail })
      vi.setSystemTime(Date.now() + cost.ms)
    }, () => false)
    clients.push(client); client.wake()
    vi.advanceTimersByTime(2000)
    const levels = frames.map(frame => frame.detail)
    expect(levels[0]).toBe(.35)
    expect(levels.at(-1)).toBe(1)
    // Each step adds at most 30 % and density never dips while it is cheap.
    for (let i = 1; i < levels.length; i++) { expect(levels[i]!).toBeGreaterThanOrEqual(levels[i - 1]!); expect(levels[i]!).toBeLessThanOrEqual(levels[i - 1]! * 1.3 + 1e-9) }
    // A device that gets busy later thins down to the floor, never changing the frame rate the loop asks for.
    cost.ms = 12; vi.advanceTimersByTime(3000)
    expect(client.detail()).toBe(.04)
    cost.ms = 0; vi.advanceTimersByTime(5000)
    expect(client.detail()).toBe(1)
  })
  it('thins unattended ink to half of its specks while it settles and restores them on the next wake', () => {
    const client = join('a'); client.wake()
    vi.advanceTimersByTime(9000)
    expect(frames.at(-1)!.detail).toBe(1)
    frames = []; vi.advanceTimersByTime(7000)
    const levels = frames.map(frame => frame.detail)
    // A gradual glide, never a step.
    for (let i = 1; i < levels.length; i++) { expect(levels[i]!).toBeLessThanOrEqual(levels[i - 1]!); expect(levels[i - 1]! - levels[i]!).toBeLessThan(.02) }
    expect(levels.at(-1)).toBe(.5)
    client.wake(); frames = []; vi.advanceTimersByTime(100)
    expect(frames[0]!.detail).toBe(1)
  })
  it('grows only as far as the drawing time allows', () => {
    // Drawing time proportional to detail: 8 ms at full density, so the ink settles near half of it.
    let detail = .35
    const client = joinMotion(document, (now, elapsed, restyle, level) => { detail = level; vi.setSystemTime(Date.now() + Math.round(level * 8)) }, () => false)
    clients.push(client); client.wake()
    vi.advanceTimersByTime(5000)
    expect(detail).toBeGreaterThanOrEqual(.35); expect(detail).toBeLessThanOrEqual(.6)
  })
  it('defers clients past the frame budget to the start of the next frame', () => {
    for (const name of ['a', 'b', 'c']) join(name, { cost: 7 }).wake()
    const perFrame: string[][] = []
    for (let i = 0; i < 3; i++) { frames = []; vi.advanceTimersToNextFrame(); perFrame.push(frames.map(frame => frame.name)) }
    expect(perFrame).toEqual([['a'], ['b'], ['c']])
  })
  it('tears the loop down with the last client and starts fresh afterwards', () => {
    const remove = vi.spyOn(document, 'removeEventListener')
    const first = join('a'); const second = join('b'); first.wake(); second.wake()
    vi.advanceTimersByTime(100); fire('dragstart')
    first.remove(); expect(remove).not.toHaveBeenCalled()
    second.remove(); first.remove()
    expect(vi.getTimerCount()).toBe(0)
    expect(remove.mock.calls.map(([type]) => type)).toEqual(['dragstart', 'dragend', 'drop', 'pointermove', 'keydown', 'beforeinput', 'input'])
    // The new loop does not inherit the old drag.
    frames = []; join('c').wake(); vi.advanceTimersByTime(100)
    expect(frames.map(frame => frame.name)).toContain('c')
  })
  it('keeps documents isolated', () => {
    const other = Object.assign(new EventTarget(), { defaultView: window }) as unknown as Document
    join('a').wake(); join('b', { doc: other }).wake()
    fire('dragstart'); fire('input')
    vi.advanceTimersByTime(400)
    expect(frames.some(frame => frame.name === 'a')).toBe(false)
    expect(Math.max(...gaps('b'))).toBeLessThanOrEqual(17)
  })
})
