// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createConcealedContent, type ConcealedContentOptions } from './index'

const instances: ReturnType<typeof createConcealedContent>[] = []
let translations: Array<[number, number]> = []
function create(options: Partial<ConcealedContentOptions> = {}) {
  const instance = createConcealedContent({ ownerDocument: document, kind: 'text', access: 'available', ...options })
  instances.push(instance); document.body.append(instance.element)
  return instance
}
function button(element: HTMLElement, label: string) {
  return Array.from(element.querySelectorAll('button')).find(node => node.textContent === label && !node.hidden)
}
function pointer(element: HTMLElement, type: string, values: Record<string, unknown> = {}) {
  const event = new Event(type, { bubbles: true, cancelable: true })
  Object.assign(event, { pointerType: 'mouse', button: 0, isPrimary: true, clientX: 20, clientY: 30, ...values })
  element.dispatchEvent(event)
}
function key(element: HTMLElement, type: string, value: string) {
  element.dispatchEvent(new KeyboardEvent(type, { key: value, bubbles: true, cancelable: true }))
}
function deferred() {
  let resolve!: (result: { element: HTMLElement; dispose?: () => void }) => void
  const promise = new Promise<{ element: HTMLElement; dispose?: () => void }>(done => { resolve = done })
  return { promise, resolve }
}
async function flush() { await Promise.resolve(); await Promise.resolve() }
function payload() { const element = document.createElement('span'); element.textContent = 'authorized-canary'; return element }

describe('concealed content', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    // Drawing time is measured with performance.now; tests simulate it by moving the system time.
    vi.spyOn(performance, 'now').mockImplementation(() => Date.now())
    translations = []
    const noop = () => {}
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({
      setTransform: noop, clearRect: noop, beginPath: noop, arc: noop, fill: noop,
      save: noop, restore: noop, translate: (x: number, y: number) => { translations.push([x, y]) }, scale: noop, fillRect: noop, drawImage: noop,
      createRadialGradient: () => ({ addColorStop: noop }), fillStyle: '', globalAlpha: 1,
    } as unknown as CanvasRenderingContext2D)
    vi.spyOn(HTMLCanvasElement.prototype, 'toDataURL').mockReturnValue('data:image/png;base64,c3ludGhldGlj')
  })
  afterEach(() => {
    instances.splice(0).forEach(instance => instance.destroy())
    vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals(); document.body.replaceChildren()
  })
  it.each([0, 7000, 45000])('uses configured %sms leave grace without an earlier ordinary inactivity timeout', async reconcealAfterMs => {
    const node = payload(); const dispose = vi.fn()
    const instance = create({ reconcealAfterMs, reveal: async () => ({ element: node, dispose }) })
    pointer(instance.element, 'pointermove'); await flush()
    pointer(instance.element, 'pointerleave')
    vi.advanceTimersByTime(reconcealAfterMs + 3999)
    expect(node.isConnected).toBe(true)
    vi.advanceTimersByTime(1)
    expect(node.isConnected).toBe(false); expect(dispose).toHaveBeenCalledTimes(1)
  })
  it('allows ordinary content to remain open with null until an explicit hide', async () => {
    const node = payload(); const dispose = vi.fn()
    const instance = create({ reconcealAfterMs: null, reveal: async () => ({ element: node, dispose }) })
    key(instance.element, 'keydown', 'Enter'); await flush()
    pointer(instance.element, 'pointerleave')
    instance.element.dispatchEvent(new FocusEvent('focusout', { relatedTarget: document.body }))
    vi.advanceTimersByTime(65000)
    expect(node.isConnected).toBe(true); expect(dispose).not.toHaveBeenCalled()
    button(instance.element, 'Hide')!.click()
    // Explicit Hide rolls the mist back smoothly before clearing.
    vi.advanceTimersByTime(800)
    expect(node.isConnected).toBe(false); expect(dispose).toHaveBeenCalledTimes(1)
  })
  it('keeps Hide clickable outside the masked content while only partly scratched', async () => {
    const node = payload(); const dispose = vi.fn()
    const instance = create({ reveal: async () => ({ element: node, dispose }) })
    const dismiss = instance.element.querySelector<HTMLElement>('[data-concealed-dismiss]')!
    expect(dismiss.hidden).toBe(true)
    pointer(instance.element, 'pointerenter'); pointer(instance.element, 'pointermove'); await flush()
    const content = instance.element.querySelector<HTMLElement>('[data-concealed-content]')!
    expect(content.inert).toBe(true)
    const hide = button(instance.element, 'Hide')!
    expect(dismiss.hidden).toBe(false)
    expect(content.contains(hide)).toBe(false)
    hide.click()
    expect(dismiss.dataset.concealedLeaving).toBe('true')
    vi.advanceTimersByTime(800)
    expect(node.isConnected).toBe(false); expect(dispose).toHaveBeenCalledTimes(1)
    expect(dismiss.hidden).toBe(true)
    expect(document.activeElement).toBe(instance.element)
  })
  it.each([null, 600000])('preserves the secret inactivity cap with reconcealAfterMs %s', async reconcealAfterMs => {
    const node = payload(); const instance = create({ kind: 'secret', reconcealAfterMs, reveal: async () => ({ element: node }) })
    button(instance.element, 'Reveal')!.click(); await flush()
    // After 30 s of inactivity the mist rolls back over 700 ms, then the value is gone.
    vi.advanceTimersByTime(30000)
    expect(node.isConnected).toBe(true)
    vi.advanceTimersByTime(750)
    expect(node.isConnected).toBe(false)
  })
  it('reveals on host request and conceals gently without moving focus', async () => {
    const node = payload(); const dispose = vi.fn(); const reveal = vi.fn(async () => ({ element: node, dispose }))
    const instance = create({ kind: 'secret', reveal })
    instance.reveal(); instance.reveal(); await flush()
    expect(reveal).toHaveBeenCalledTimes(1); expect(node.isConnected).toBe(true)
    const outside = document.createElement('button'); document.body.append(outside); outside.focus()
    instance.conceal()
    expect(node.isConnected).toBe(true)
    vi.advanceTimersByTime(750)
    expect(node.isConnected).toBe(false); expect(dispose).toHaveBeenCalledOnce()
    expect(document.activeElement).toBe(outside)
  })
  it('lets hosts that ask for a second factor wait longer than the default release deadline', async () => {
    let signal!: AbortSignal
    const instance = create({ kind: 'secret', revealTimeoutMs: 120000, reveal: context => { signal = context.signal; return new Promise(() => {}) } })
    button(instance.element, 'Reveal')!.click(); await flush()
    vi.advanceTimersByTime(60000); expect(signal.aborted).toBe(false)
    vi.advanceTimersByTime(60000); expect(signal.aborted).toBe(true)
    for (const revealTimeoutMs of [999, 600001, Number.NaN]) expect(() => create({ kind: 'secret', revealTimeoutMs })).toThrow(RangeError)
  })
  it('keeps the pending authorization deadline even when automatic covering is disabled', async () => {
    const pending = deferred(); let signal!: AbortSignal; const dispose = vi.fn()
    const instance = create({ reconcealAfterMs: null, reveal: context => { signal = context.signal; return pending.promise } })
    pointer(instance.element, 'pointermove'); pointer(instance.element, 'pointerleave')
    vi.advanceTimersByTime(30000)
    expect(signal.aborted).toBe(true)
    const node = payload(); pending.resolve({ element: node, dispose }); await flush()
    expect(node.isConnected).toBe(false); expect(dispose).toHaveBeenCalledTimes(1)
  })
  it.each([-1, NaN, Infinity, 600001, '1500'])('rejects invalid delay %s before creating DOM or listeners', reconcealAfterMs => {
    const createElement = vi.spyOn(document, 'createElement')
    const addListener = vi.spyOn(document, 'addEventListener')
    expect(() => create({ reconcealAfterMs } as Partial<ConcealedContentOptions>)).toThrow(RangeError)
    expect(createElement).not.toHaveBeenCalled(); expect(addListener).not.toHaveBeenCalled()
  })
  it.each(['keyboard', 'touch'])('adds the denial icon and explicit request together after the %s hold threshold', input => {
    const requestAccess = vi.fn(); const instance = create({ access: 'requestable', requestAccess })
    if (input === 'keyboard') key(instance.element, 'keydown', ' ')
    else pointer(instance.element, 'pointerdown', { pointerType: 'touch' })
    vi.advanceTimersByTime(849)
    expect(instance.element.querySelector('[data-concealed-request]')).toBeNull()
    vi.advanceTimersByTime(1)
    const request = button(instance.element, 'Request access')!
    expect(request).toBeDefined()
    expect(request.querySelector('svg[aria-hidden="true"]')).not.toBeNull()
    expect(requestAccess).not.toHaveBeenCalled()
    request.click(); expect(requestAccess).toHaveBeenCalledTimes(1)
  })
  it('settles denied feedback after a successful request, including later focus and pointer events', () => {
    const instance = create({ access: 'requestable', requestAccess: () => {} })
    pointer(instance.element, 'pointerenter'); vi.advanceTimersByTime(850)
    expect(instance.element.dataset.concealedDenied).toBe('true')
    button(instance.element, 'Request access')!.click()
    pointer(instance.element, 'pointermove'); instance.element.focus()
    expect(instance.element.dataset.concealedDenied).toBeUndefined()
    expect(instance.element.textContent).toContain('Request pending')
    expect(button(instance.element, 'Request access')).toBeUndefined()
  })
  it.each(['abort', 'visibility'])('still clears immediately on %s with automatic covering disabled', async action => {
    const controller = new AbortController(); const node = payload(); const dispose = vi.fn()
    const instance = create({ reconcealAfterMs: null, signal: controller.signal, reveal: async () => ({ element: node, dispose }) })
    key(instance.element, 'keydown', 'Enter'); await flush()
    if (action === 'abort') controller.abort()
    else { vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden'); document.dispatchEvent(new Event('visibilitychange')) }
    expect(node.isConnected).toBe(false); expect(dispose).toHaveBeenCalledTimes(1)
  })
  it('starts with synthetic shapes and no visible reveal control, payload or image URL', () => {
    const reveal = vi.fn(async () => { const element = document.createElement('img'); element.src = 'https://example.test/hidden-canary.png'; return { element } })
    const instance = create({ kind: 'image', reveal })
    expect(button(instance.element, 'Reveal')).toBeUndefined()
    expect(instance.element.tabIndex).toBe(0)
    expect(instance.element.getAttribute('role')).not.toBe('button')
    expect(instance.element.getAttribute('aria-label')).toContain('Enter')
    expect(reveal).not.toHaveBeenCalled()
    expect(document.querySelector('img')).toBeNull()
    expect(document.body.innerHTML).not.toContain('hidden-canary')
  })
  it('provides inline phrasing markup without constructing payload', () => {
    const reveal = vi.fn(); const instance = create({ layout: 'inline', kind: 'secret', reveal })
    expect(instance.element.tagName).toBe('SPAN')
    expect(instance.element.dataset.concealedLayout).toBe('inline')
    expect(instance.element.querySelector('div')).toBeNull()
    expect(instance.element.getAttribute('role')).toBe('group')
    expect(reveal).not.toHaveBeenCalled()
  })
  it('uses a plain inline-secret tap only to expose controls without releasing the value', async () => {
    const reveal = vi.fn(async () => {})
    const instance = create({ layout: 'inline', kind: 'secret', reveal })
    instance.element.click(); await flush()
    expect(instance.element.dataset.concealedActionsOpen).toBe('true')
    expect(reveal).not.toHaveBeenCalled()
    pointer(document.body, 'pointerdown')
    expect(instance.element.dataset.concealedActionsOpen).toBeUndefined()
  })
  it('keeps secret actions in one slide-in rail that also accepts host controls', () => {
    const instance = create({ kind: 'secret', reveal: async () => {} })
    const rail = instance.element.querySelector<HTMLElement>('[data-concealed-rail]')!
    expect(instance.actions).toBe(rail)
    expect(rail.contains(button(instance.element, 'Reveal')!)).toBe(true)
    expect(instance.element.querySelector('.md-concealed__controls')!.contains(rail)).toBe(false)
  })
  it('shows the password request at rest and sends it only from its button', async () => {
    let finish!: () => void
    const requestAccess = vi.fn(() => new Promise<void>(resolve => { finish = resolve }))
    const reveal = vi.fn()
    const instance = create({ kind: 'secret', access: 'requestable', requestAccess, reveal })
    // No gesture needed: the lock button and its message are there before any interaction.
    const request = button(instance.element, 'Request access')!
    expect(instance.actions.contains(request)).toBe(true)
    expect(request.dataset.concealedAction).toBe('request')
    expect(request.querySelector('svg[aria-hidden="true"]')).not.toBeNull()
    expect(instance.element.dataset.concealedRequestState).toBe('idle')
    expect(instance.element.textContent).toContain('The values are hidden')
    expect(instance.element.getAttribute('aria-label')).toBe('The values are hidden. You can request access.')
    expect(button(instance.element, 'Reveal')).toBeUndefined()
    // Leaving, blurring and hiding keep it where it is.
    pointer(instance.element, 'pointerenter'); pointer(instance.element, 'pointerleave')
    instance.element.dispatchEvent(new FocusEvent('focusout', { relatedTarget: document.body })); instance.hide()
    vi.advanceTimersByTime(2000)
    expect(button(instance.element, 'Request access')).toBe(request)
    instance.element.click(); expect(requestAccess).not.toHaveBeenCalled()
    request.click(); request.click()
    expect(requestAccess).toHaveBeenCalledTimes(1)
    expect(instance.element.dataset.concealedRequestState).toBe('sending')
    expect(instance.element.textContent).toContain('Sending request')
    finish(); await flush()
    expect(instance.element.dataset.concealedRequestState).toBe('pending')
    expect(instance.element.textContent).toContain('Request pending')
    // The request control fades out instead of vanishing.
    expect(request.dataset.concealedLeaving).toBe('true')
    vi.advanceTimersByTime(400)
    expect(button(instance.element, 'Request access')).toBeUndefined()
    instance.hide()
    expect(button(instance.element, 'Request access')).toBeUndefined()
    expect(instance.element.textContent).toContain('Request pending')
    expect(reveal).not.toHaveBeenCalled()
  })
  it('offers a new password request after a refused one and keeps the button after a failure', async () => {
    const requestAccess = vi.fn().mockRejectedValueOnce(new Error('offline')).mockResolvedValue(undefined)
    const instance = create({ kind: 'secret', access: 'requestable', requestAccess, requestRefused: true, labels: { refused: 'Not approved' } })
    expect(instance.element.dataset.concealedRequestState).toBe('refused')
    expect(instance.element.textContent).toContain('Not approved')
    button(instance.element, 'Request access')!.click(); await flush()
    expect(instance.element.dataset.concealedRequestState).toBe('failed')
    expect(instance.element.textContent).toContain('Unable to request access')
    button(instance.element, 'Request access')!.click(); await flush()
    expect(instance.element.dataset.concealedRequestState).toBe('pending')
    expect(requestAccess).toHaveBeenCalledTimes(2)
    // A pending request from the host wins over an old refusal.
    expect(create({ kind: 'secret', access: 'requestable', requestPending: true, requestRefused: true }).element.dataset.concealedRequestState).toBe('pending')
  })
  it('asks confirmRequest before sending, and a cancelled confirmation sends nothing', async () => {
    const settle = async () => { for (let i = 0; i < 6; i++) await Promise.resolve() }
    const requestAccess = vi.fn()
    const answers: Array<(confirmed: boolean) => void> = []
    const contexts: { anchor: HTMLElement; signal: AbortSignal }[] = []
    const confirmRequest = vi.fn((context: { anchor: HTMLElement; signal: AbortSignal }) => {
      contexts.push(context); return new Promise<boolean>(resolve => answers.push(resolve))
    })
    const instance = create({ kind: 'secret', access: 'requestable', requestAccess, confirmRequest })
    const request = button(instance.element, 'Request access')!
    request.click(); request.click(); await settle()
    expect(confirmRequest).toHaveBeenCalledTimes(1)
    expect(contexts[0]!.anchor).toBe(instance.element)
    expect(request.disabled).toBe(true)
    expect(requestAccess).not.toHaveBeenCalled()
    expect(instance.element.textContent).not.toContain('Sending request')
    // Focus moving into the host's panel keeps the rail and its request button.
    instance.element.dispatchEvent(new FocusEvent('focusout', { relatedTarget: document.body }))
    expect(instance.element.dataset.concealedActionsOpen).toBe('true')
    answers[0]!(false); await settle()
    expect(requestAccess).not.toHaveBeenCalled()
    expect(request.disabled).toBe(false); expect(request.hidden).toBe(false)
    expect(instance.element.textContent).not.toContain('Unable to request access')
    request.click(); await settle()
    answers[1]!(true); await settle()
    expect(requestAccess).toHaveBeenCalledTimes(1)
    expect(instance.element.textContent).toContain('Request pending')
    // Hiding the surface (window blur, Escape) aborts a confirmation in progress; its late answer sends nothing.
    const other = create({ kind: 'secret', access: 'requestable', requestAccess, confirmRequest })
    button(other.element, 'Request access')!.click(); await settle()
    other.hide()
    expect(contexts[2]!.signal.aborted).toBe(true)
    answers[2]!(true); await settle()
    expect(requestAccess).toHaveBeenCalledTimes(1)
  })
  it('marks the red denied and blue pending ink, including a request pending after reload', () => {
    const denied = create({ kind: 'secret', access: 'requestable', requestAccess: vi.fn() })
    expect(denied.element.dataset.concealedAccess).toBe('denied')
    button(denied.element, 'Request access')!.click()
    expect(denied.element.dataset.concealedAccess).toBe('pending')
    denied.hide()
    expect(denied.element.dataset.concealedAccess).toBe('pending')
    expect(create({ kind: 'secret', access: 'requestable', requestPending: true }).element.dataset.concealedAccess).toBe('pending')
    expect(create({ kind: 'secret' }).element.dataset.concealedAccess).toBeUndefined()
  })
  it('lets Tab reach the password request directly and Enter send it', () => {
    const requestAccess = vi.fn(); const instance = create({ kind: 'secret', access: 'requestable', requestAccess })
    expect(instance.element.tabIndex).toBe(-1)
    const request = button(instance.element, 'Request access')!
    expect(request.tabIndex).toBe(0)
    // Enter on the surface itself (focused by a script) moves focus to the button without sending.
    instance.element.focus(); key(instance.element, 'keydown', 'Enter')
    expect(document.activeElement).toBe(request)
    expect(requestAccess).not.toHaveBeenCalled()
    key(request, 'keydown', 'Enter')
    expect(requestAccess).toHaveBeenCalledTimes(1)
  })
  it('keeps mouse scratches revealed after button release while the pointer is still inside', async () => {
    const node = payload(); const instance = create({ reveal: async () => ({ element: node }) })
    pointer(instance.element, 'pointerenter'); pointer(instance.element, 'pointerdown'); pointer(instance.element, 'pointermove'); await flush()
    pointer(instance.element, 'pointerup'); vi.advanceTimersByTime(4200)
    expect(node.isConnected).toBe(true)
    expect(instance.element.dataset.concealedCoverage).toBe('1')
  })
  it('supports an optional visible reveal control', async () => {
    const node = payload(); const instance = create({ showRevealControl: true, reveal: async () => ({ element: node }) })
    button(instance.element, 'Reveal')!.click(); await flush()
    expect(node.isConnected).toBe(true)
    expect(instance.element.querySelector<HTMLElement>('[data-concealed-content]')!.style.maskImage).toBe('')
  })
  it.each([' ', 'Enter'])('deliberately reveals via keyboard %s and hides on Escape', async value => {
    const dispose = vi.fn(); const node = payload()
    const instance = create({ reveal: async () => ({ element: node, dispose }) })
    instance.element.focus(); key(instance.element, 'keydown', value); await flush()
    expect(node.isConnected).toBe(true)
    key(instance.element, 'keydown', 'Escape')
    expect(node.isConnected).toBe(false); expect(dispose).toHaveBeenCalledTimes(1)
  })
  it('accumulates scratches without evicting earlier areas or reloading while hovered', async () => {
    const node = payload(); const reveal = vi.fn(async () => ({ element: node }))
    const instance = create({ reveal })
    pointer(instance.element, 'pointerenter')
    for (let i = 0; i < 60; i++) pointer(instance.element, 'pointermove', { clientX: i * 5, clientY: 40 })
    await flush(); vi.advanceTimersByTime(5000)
    const content = instance.element.querySelector<HTMLElement>('[data-concealed-content]')!
    expect(node.isConnected).toBe(true)
    expect(content.style.maskImage).toContain('url(')
    expect(reveal).toHaveBeenCalledTimes(1)
    expect(instance.element.querySelectorAll('canvas')).toHaveLength(1)
  })
  it('keeps scratches for 1500ms after leave, then slowly covers and disposes after 4000ms', async () => {
    const node = payload(); const dispose = vi.fn()
    const instance = create({ reveal: async () => ({ element: node, dispose }) })
    pointer(instance.element, 'pointermove'); await flush()
    pointer(instance.element, 'pointerleave')
    vi.advanceTimersByTime(1499); expect(node.isConnected).toBe(true)
    expect(instance.element.dataset.concealedCoverage).toBe('1')
    vi.advanceTimersByTime(1000)
    expect(Number(instance.element.dataset.concealedCoverage)).toBeGreaterThan(0)
    expect(Number(instance.element.dataset.concealedCoverage)).toBeLessThan(1)
    vi.advanceTimersByTime(3100)
    expect(node.isConnected).toBe(false); expect(dispose).toHaveBeenCalledTimes(1)
  })
  it('restores accumulated scratches when reentered during cover and extends without reloading', async () => {
    const node = payload(); const reveal = vi.fn(async () => ({ element: node }))
    const instance = create({ reveal })
    pointer(instance.element, 'pointermove'); await flush(); pointer(instance.element, 'pointerleave')
    vi.advanceTimersByTime(2300)
    pointer(instance.element, 'pointerenter')
    expect(instance.element.dataset.concealedCoverage).toBe('1')
    pointer(instance.element, 'pointermove', { clientX: 160 })
    vi.advanceTimersByTime(4000)
    expect(node.isConnected).toBe(true); expect(reveal).toHaveBeenCalledTimes(1)
  })
  it('preserves touch strokes during grace, then covers after lift', async () => {
    const node = payload(); const reveal = vi.fn(async () => ({ element: node }))
    const instance = create({ reveal })
    pointer(instance.element, 'pointerdown', { pointerType: 'touch' })
    pointer(instance.element, 'pointermove', { pointerType: 'touch' }); await flush()
    pointer(instance.element, 'pointerup', { pointerType: 'touch' }); pointer(instance.element, 'pointerleave', { pointerType: 'touch' })
    vi.advanceTimersByTime(800)
    pointer(instance.element, 'pointerdown', { pointerType: 'touch', clientX: 150 })
    pointer(instance.element, 'pointermove', { pointerType: 'touch', clientX: 180 })
    pointer(instance.element, 'pointerup', { pointerType: 'touch' })
    expect(reveal).toHaveBeenCalledTimes(1); expect(node.isConnected).toBe(true)
    vi.advanceTimersByTime(5700); expect(node.isConnected).toBe(false)
  })
  it('covers keyboard full reveal after focus leaves', async () => {
    const node = payload(); const instance = create({ reveal: async () => ({ element: node }) })
    instance.element.focus(); key(instance.element, 'keydown', 'Enter'); await flush()
    instance.element.dispatchEvent(new FocusEvent('focusout', { bubbles: true, relatedTarget: document.body }))
    vi.advanceTimersByTime(5700); expect(node.isConnected).toBe(false)
  })
  it('bounds full reveal lifetime and does not auto reload on movement after timeout', async () => {
    const node = payload(); const reveal = vi.fn(async () => ({ element: node }))
    const instance = create({ reveal })
    pointer(instance.element, 'pointerenter'); key(instance.element, 'keydown', 'Enter'); await flush()
    vi.advanceTimersByTime(30800); expect(node.isConnected).toBe(false)
    pointer(instance.element, 'pointermove'); await flush()
    expect(reveal).toHaveBeenCalledTimes(1)
  })
  it.each(['hide', 'destroy', 'abort', 'tab-hidden', 'pointercancel', 'grace', 'timeout'])('disposes a delayed result after %s without mounting', async action => {
    const pending = deferred(); const dispose = vi.fn(); const controller = new AbortController()
    let requestSignal!: AbortSignal
    const instance = create({ signal: controller.signal, reveal: context => { requestSignal = context.signal; return pending.promise } })
    pointer(instance.element, 'pointermove')
    if (action === 'hide') instance.hide()
    if (action === 'destroy') instance.destroy()
    if (action === 'abort') controller.abort()
    if (action === 'tab-hidden') { vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden'); document.dispatchEvent(new Event('visibilitychange')) }
    if (action === 'pointercancel') pointer(instance.element, 'pointercancel')
    if (action === 'grace') { pointer(instance.element, 'pointerleave'); vi.advanceTimersByTime(5700) }
    if (action === 'timeout') vi.advanceTimersByTime(30100)
    expect(requestSignal.aborted).toBe(true)
    const node = payload(); pending.resolve({ element: node, dispose }); await flush()
    expect(node.isConnected).toBe(false); expect(dispose).toHaveBeenCalledTimes(1)
  })
  it('requires the visible button for secret release even when scratch is explicitly enabled', async () => {
    const reveal = vi.fn(async () => {})
    const instance = create({ kind: 'secret', scratchToReveal: true, reveal })
    pointer(instance.element, 'pointermove'); expect(reveal).not.toHaveBeenCalled()
    instance.element.click(); key(instance.element, 'keydown', 'Enter'); await flush()
    expect(reveal).not.toHaveBeenCalled()
    button(instance.element, 'Reveal')!.click(); await flush(); expect(reveal).toHaveBeenCalledTimes(1)
  })
  it('keeps lottery-ticket scratching and explicit keyboard reveal with reduced motion', async () => {
    vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: true })))
    const reveal = vi.fn(async () => ({ element: payload() }))
    const instance = create({ reveal })
    expect(instance.element.dataset.concealedReducedMotion).toBe('true')
    // Scratching is direct manipulation, so it still clears only the touched part of the fog.
    pointer(instance.element, 'pointerenter'); pointer(instance.element, 'pointermove'); await flush()
    expect(reveal).toHaveBeenCalledTimes(1)
    const content = instance.element.querySelector<HTMLElement>('[data-concealed-content]')!
    expect(content.inert).toBe(true)
    expect(content.style.maskImage).toContain('url(')
    key(instance.element, 'keydown', 'Enter'); await flush()
    expect(content.inert).toBe(false)
    expect(reveal).toHaveBeenCalledTimes(1)
  })
  it('shows denied feedback and only inserts the request action after the hold', () => {
    const reveal = vi.fn(); const requestAccess = vi.fn()
    const instance = create({ access: 'requestable', reveal, requestAccess })
    pointer(instance.element, 'pointerenter'); pointer(instance.element, 'pointermove')
    expect(instance.element.dataset.concealedDenied).toBe('true')
    expect(instance.element.textContent).toContain('You do not have access')
    expect(instance.element.querySelector<HTMLElement>('[data-concealed-particles]')!.style.maskImage).toBe('')
    vi.advanceTimersByTime(849); expect(button(instance.element, 'Request access')).toBeUndefined()
    vi.advanceTimersByTime(1); expect(button(instance.element, 'Request access')).toBeDefined()
    expect(reveal).not.toHaveBeenCalled(); expect(requestAccess).not.toHaveBeenCalled()
    const request = button(instance.element, 'Request access')!
    pointer(request, 'pointerdown'); pointer(request, 'pointerup'); request.click()
    expect(requestAccess).toHaveBeenCalledTimes(1)
  })
  it.each(['pointerleave', 'pointerup', 'pointercancel', 'blur'])('cancels incomplete request holds on %s', event => {
    const instance = create({ access: 'requestable' })
    pointer(instance.element, 'pointerdown', { pointerType: 'touch' }); vi.advanceTimersByTime(400)
    instance.element.dispatchEvent(new Event(event)); vi.advanceTimersByTime(600)
    expect(button(instance.element, 'Request access')).toBeUndefined()
  })
  it('supports a keyboard request hold and focuses the discovered action without submitting', () => {
    const requestAccess = vi.fn(); const instance = create({ access: 'requestable', requestAccess })
    instance.element.focus(); key(instance.element, 'keydown', ' '); vi.advanceTimersByTime(850); key(instance.element, 'keyup', ' ')
    expect(document.activeElement).toBe(button(instance.element, 'Request access'))
    expect(requestAccess).not.toHaveBeenCalled()
  })
  it('preserves an armed touch request on lift and leave for a second tap', () => {
    const requestAccess = vi.fn(); const instance = create({ access: 'requestable', requestAccess })
    pointer(instance.element, 'pointerdown', { pointerType: 'touch' }); vi.advanceTimersByTime(850)
    pointer(instance.element, 'pointerup', { pointerType: 'touch' }); pointer(instance.element, 'pointerleave', { pointerType: 'touch' })
    const request = button(instance.element, 'Request access')!
    expect(request.isConnected).toBe(true)
    pointer(request, 'pointerdown', { pointerType: 'touch' }); pointer(request, 'pointerup', { pointerType: 'touch' }); request.click()
    expect(requestAccess).toHaveBeenCalledTimes(1)
  })
  it('sanitizes reveal failures and keeps the next scratch masked', async () => {
    let calls = 0; const instance = create({ reveal: async () => { if (++calls === 1) throw new Error('private-canary'); return { element: payload() } } })
    key(instance.element, 'keydown', 'Enter'); await flush()
    expect(instance.element.textContent).toContain('Unable to reveal content')
    expect(instance.element.textContent).not.toContain('private-canary')
    pointer(instance.element, 'pointermove'); await flush()
    expect(instance.element.querySelector<HTMLElement>('[data-concealed-content]')!.style.maskImage).toContain('url(')
  })
  it('disposes once even when host cleanup reenters destroy', async () => {
    const node = payload(); const dispose = vi.fn(() => instance.destroy())
    const instance = create({ reveal: async () => ({ element: node, dispose }) })
    key(instance.element, 'keydown', 'Enter'); await flush(); instance.hide()
    expect(dispose).toHaveBeenCalledTimes(1); expect(node.isConnected).toBe(false)
  })
  it('never offers another request when access is already pending', () => {
    const requestAccess = vi.fn(); const instance = create({ access: 'requestable', requestPending: true, requestAccess })
    pointer(instance.element, 'pointerenter'); key(instance.element, 'keydown', ' '); vi.advanceTimersByTime(1000)
    expect(button(instance.element, 'Request access')).toBeUndefined()
    expect(instance.element.textContent).toContain('Request pending')
    expect(requestAccess).not.toHaveBeenCalled()
  })
  it('guards duplicate requests synchronously and shows successful pending state', async () => {
    let finish!: () => void
    const pending = new Promise<void>(resolve => { finish = resolve })
    const requestAccess = vi.fn(() => pending)
    const instance = create({ access: 'requestable', requestAccess })
    pointer(instance.element, 'pointerenter'); vi.advanceTimersByTime(850)
    const request = button(instance.element, 'Request access')!
    request.click(); request.dispatchEvent(new Event('click'))
    expect(requestAccess).toHaveBeenCalledTimes(1)
    expect(instance.element.textContent).toContain('Sending request')
    finish(); await flush()
    expect(instance.element.textContent).toContain('Request pending')
    expect(button(instance.element, 'Request access')).toBeUndefined()
  })
  it('treats a synchronous successful request as pending and does not rearm', () => {
    const requestAccess = vi.fn(); const instance = create({ access: 'requestable', requestAccess })
    pointer(instance.element, 'pointerenter'); vi.advanceTimersByTime(850); button(instance.element, 'Request access')!.click()
    pointer(instance.element, 'pointerleave'); pointer(instance.element, 'pointerenter'); vi.advanceTimersByTime(850)
    expect(instance.element.textContent).toContain('Request pending')
    expect(button(instance.element, 'Request access')).toBeUndefined(); expect(requestAccess).toHaveBeenCalledTimes(1)
  })
  it('sanitizes asynchronous request rejection and enables an explicit retry', async () => {
    const requestAccess = vi.fn(async () => { throw new Error('private-request-canary') })
    const instance = create({ access: 'requestable', requestAccess })
    pointer(instance.element, 'pointerenter'); vi.advanceTimersByTime(850); button(instance.element, 'Request access')!.click(); await flush()
    expect(instance.element.textContent).toContain('Unable to request access')
    expect(instance.element.textContent).not.toContain('private-request-canary')
    button(instance.element, 'Request access')!.click(); await flush()
    expect(requestAccess).toHaveBeenCalledTimes(2)
  })
  it.each(['hide', 'destroy'])('ignores late request UI completion after %s', async action => {
    let finish!: () => void
    const pending = new Promise<void>(resolve => { finish = resolve })
    const instance = create({ access: 'requestable', requestAccess: () => pending })
    pointer(instance.element, 'pointerenter'); vi.advanceTimersByTime(850); button(instance.element, 'Request access')!.click()
    if (action === 'hide') instance.hide(); else instance.destroy()
    finish(); await flush()
    expect(instance.element.textContent).not.toContain('Request pending')
  })
  it('parts secret fog only after an explicit button returns authorized content', async () => {
    const pending = deferred(); const node = payload()
    const instance = create({ kind: 'secret', showRevealControl: false, reveal: () => pending.promise })
    expect(button(instance.element, 'Reveal')).toBeDefined()
    button(instance.element, 'Reveal')!.click()
    vi.advanceTimersByTime(400)
    expect(instance.element.querySelector('[data-concealed-content]')).toBeNull()
    expect(instance.element.querySelector<HTMLElement>('[data-concealed-particles]')!.style.maskImage).toBe('')
    pending.resolve({ element: node }); await flush()
    const content = instance.element.querySelector<HTMLElement>('[data-concealed-content]')!
    expect(content.inert).toBe(true)
    expect(content.style.maskImage).toContain('url(')
    vi.advanceTimersByTime(900)
    expect(content.inert).toBe(false)
    expect(content.style.maskImage).toBe('')
    expect(node.isConnected).toBe(true)
  })
  it('varies the secret opening ring origin using synthetic randomness', async () => {
    const first = create({ kind: 'secret', reveal: async () => ({ element: payload() }) })
    const random = vi.spyOn(Math, 'random').mockReturnValue(.15)
    translations = []; button(first.element, 'Reveal')!.click(); await flush()
    const firstOrigins = translations.slice()
    first.destroy()
    random.mockRestore()
    const second = create({ kind: 'secret', reveal: async () => ({ element: payload() }) })
    vi.spyOn(Math, 'random').mockReturnValue(.8)
    translations = []; button(second.element, 'Reveal')!.click(); await flush()
    expect(firstOrigins.length).toBeGreaterThan(0)
    expect(translations).not.toEqual(firstOrigins)
  })
  it('cancels secret parting and disposes content immediately on hide', async () => {
    const node = payload(); const dispose = vi.fn()
    const instance = create({ kind: 'secret', reveal: async () => ({ element: node, dispose }) })
    button(instance.element, 'Reveal')!.click(); await flush(); vi.advanceTimersByTime(300)
    instance.hide(); vi.advanceTimersByTime(900)
    expect(node.isConnected).toBe(false); expect(dispose).toHaveBeenCalledTimes(1)
    expect(instance.element.querySelector('[data-concealed-content]')).toBeNull()
    instance.destroy(); expect(vi.getTimerCount()).toBe(0)
  })
  it('reveals authorized secrets immediately with reduced motion', async () => {
    vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: true })))
    const instance = create({ kind: 'secret', reveal: async () => ({ element: payload() }) })
    button(instance.element, 'Reveal')!.click(); await flush()
    const content = instance.element.querySelector<HTMLElement>('[data-concealed-content]')!
    expect(content.style.maskImage).toBe(''); expect(content.inert).toBe(false)
    expect(translations).toHaveLength(0)
  })
  it('stops canvas work when detached/destroyed and isolates instances', () => {
    const first = create(); const second = create()
    pointer(first.element, 'pointerenter'); pointer(first.element, 'pointermove')
    first.element.remove(); vi.advanceTimersByTime(80); first.destroy(); second.destroy()
    expect(vi.getTimerCount()).toBe(0)
  })
  it('re-reads theme colours rarely and never while the document is being typed in', () => {
    const instance = create()
    const styles = vi.spyOn(window, 'getComputedStyle')
    pointer(instance.element, 'pointerenter')
    for (let i = 0; i < 30; i++) { document.dispatchEvent(new Event('beforeinput')); vi.advanceTimersByTime(100) }
    expect(styles).not.toHaveBeenCalled()
    // One palette read (five probe colours) once typing stops, then nothing for five seconds.
    vi.advanceTimersByTime(5000)
    expect(styles).toHaveBeenCalledTimes(5)
  })
  it('fills the ink in gradually while drawing is cheap and thins it gradually once it is costly', () => {
    // Specks live for random times and glint by the time of day, and the fake clock starts at the real one (its frames
    // fall on that start's 16 ms grid), so how many are drawn and leave in one frame differed from run to run: about
    // one in 400 lost 10 of 122 in a frame (CI run 154). A clock started at a fixed time and a seeded sequence
    // (mulberry32) make every run the same one.
    vi.useFakeTimers({ now: new Date('2026-01-01T00:00:00Z') })
    // The scheduler measures drawing with performance.now(); tie it to the fake clock so only the simulated cost
    // counts, not how fast the machine running the test happens to be (a slow CI runner thinned in visible steps).
    vi.spyOn(performance, 'now').mockImplementation(() => Date.now())
    let seed = 1
    vi.spyOn(Math, 'random').mockImplementation(() => {
      let t = seed += 0x6D2B79F5
      t = Math.imul(t ^ t >>> 15, t | 1); t ^= t + Math.imul(t ^ t >>> 7, t | 61)
      return ((t ^ t >>> 14) >>> 0) / 4294967296
    })
    let drawn = 0; let slow = false
    const noop = () => {}
    vi.mocked(HTMLCanvasElement.prototype.getContext).mockReturnValue({
      setTransform: noop, clearRect: noop, save: noop, restore: noop, translate: noop, scale: noop, drawImage: noop,
      // On the slow device every fifty specks drawn cost a millisecond: far over the ink's share of a frame.
      fillRect: () => { drawn++; if (slow && drawn % 50 === 0) vi.setSystemTime(Date.now() + 1) },
      createRadialGradient: () => ({ addColorStop: noop }), fillStyle: '', globalAlpha: 1,
    } as unknown as CanvasRenderingContext2D)
    pointer(create().element, 'pointerenter')
    function frames(count: number) {
      const counts: number[] = []
      for (let i = 0; i < count; i++) { const before = drawn; vi.advanceTimersToNextFrame(); if (drawn > before) counts.push(drawn - before) }
      return counts
    }
    const average = (counts: number[]) => counts.reduce((sum, count) => sum + count, 0) / counts.length
    const growing = frames(120)
    const full = average(frames(20))
    // A 320×160 surface calls for about 1,700 specks; it starts with about a third of them and adds the rest.
    expect(full).toBeGreaterThan(1650)
    expect(average(growing.slice(0, 5))).toBeLessThan(full * .45)
    slow = true
    const thinning = frames(240)
    expect(average(frames(20))).toBeLessThan(full * .5)
    // Specks leave one by one as they fade out, never in visible steps.
    for (let i = 1; i < thinning.length; i++) expect(thinning[i]!).toBeGreaterThan(thinning[i - 1]! * .92)
  })
  it('draws only a window around the viewport on a surface far larger than it, and moves it with the scroll', () => {
    const resized: ResizeObserverCallback[] = []
    vi.stubGlobal('ResizeObserver', class { constructor(callback: ResizeObserverCallback) { resized.push(callback) } observe() {} disconnect() {} })
    const instance = create()
    let top = -3000
    vi.spyOn(instance.element, 'getBoundingClientRect').mockImplementation(() => ({ top, left: 0, width: 800, height: 6000 }) as DOMRect)
    for (const callback of resized) callback([{ borderBoxSize: [{ inlineSize: 800, blockSize: 6000 }] }] as unknown as ResizeObserverEntry[], {} as ResizeObserver)
    const canvas = instance.element.querySelector<HTMLCanvasElement>('[data-concealed-canvas]')!
    // A viewport and a half tall, whatever the surface: the canvas stays within browser limits and sharp.
    const windowHeight = Math.ceil(innerHeight * 1.5)
    expect(canvas.style.height).toBe(`${windowHeight}px`); expect(canvas.height).toBe(windowHeight)
    pointer(instance.element, 'pointerenter'); vi.advanceTimersToNextFrame()
    const centred = Math.round(3000 - (windowHeight - innerHeight) / 2)
    expect(canvas.style.transform).toBe(`translate(0px, ${centred}px)`)
    top = -3400; document.dispatchEvent(new Event('scroll')); vi.advanceTimersToNextFrame()
    expect(canvas.style.transform).toBe(`translate(0px, ${centred + 400}px)`)
  })
  it('morphs one secret toggle between Reveal and Hide and offers an optional copy icon', async () => {
    const node = payload(); const dispose = vi.fn(); const copy = vi.fn()
    const instance = create({ kind: 'secret', reveal: async () => ({ element: node, dispose }), copy })
    const toggle = instance.element.querySelector<HTMLButtonElement>('[data-concealed-action="reveal"]')!
    expect(toggle.textContent).toBe('Reveal'); expect(toggle.dataset.concealedMode).toBe('reveal')
    toggle.click(); await flush()
    expect(node.isConnected).toBe(true)
    // The same button now reads Hide; no separate corner button appears for secrets.
    expect(toggle.textContent).toBe('Hide'); expect(toggle.dataset.concealedMode).toBe('hide')
    expect(instance.element.querySelector<HTMLElement>('[data-concealed-dismiss]')!.hidden).toBe(true)
    const copyButton = instance.element.querySelector<HTMLButtonElement>('.md-concealed__copy')!
    expect(copyButton.getAttribute('aria-label')).toBe('Copy')
    copyButton.click(); await flush()
    expect(copy).toHaveBeenCalledOnce(); expect(copyButton.dataset.concealedDone).toBe('true')
    vi.advanceTimersByTime(1700); expect(copyButton.dataset.concealedDone).toBeUndefined()
    toggle.click(); expect(toggle.textContent).toBe('Reveal')
    vi.advanceTimersByTime(800)
    expect(node.isConnected).toBe(false); expect(dispose).toHaveBeenCalledOnce()
    expect(create({ kind: 'secret', reveal: async () => {} }).element.querySelector('.md-concealed__copy')).toBeNull()
  })
  it('keeps the toggle on Hide while a host shows the secret and asks the host to close it', () => {
    const onHide = vi.fn(); const instance = create({ kind: 'secret', reveal: async () => {} })
    instance.setActive(true, onHide)
    expect(instance.element.dataset.concealedHostOpen).toBe('true')
    button(instance.element, 'Hide')!.click(); expect(onHide).toHaveBeenCalledOnce()
    instance.setActive(false)
    expect(instance.element.dataset.concealedHostOpen).toBeUndefined()
    expect(button(instance.element, 'Reveal')).toBeDefined()
  })
})
