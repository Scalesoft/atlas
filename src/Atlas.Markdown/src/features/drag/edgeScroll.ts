import type { EditorView } from '@tiptap/pm/view'

/**
 * Edge-scroll while dragging. When the cursor sits inside the top/bottom
 * EDGE_THRESHOLD_PX strip of the scroll container, run a rAF loop that
 * scrolls the container at a velocity that ramps linearly with how deep
 * the cursor is inside the strip. Tuned for "slow" — max 240 px/s — so
 * dropping near a screen edge during a long-doc reorder no longer requires
 * the user to release, scroll manually, re-grab.
 *
 * The scroll target is the nearest ancestor of `view.dom` with
 * `overflow-y: auto|scroll` AND content overflowing its viewport. This
 * picks up the fullscreen editor's own scroll container (`.md-editor.is-
 * fullscreen` is `overflow-y: auto` while the body is locked) and otherwise
 * falls back to the window for normal in-page editors.
 */
const EDGE_THRESHOLD_PX = 80
const EDGE_MAX_VELOCITY_PX_PER_SECOND = 240
const EDGE_MIN_VELOCITY_PX_PER_SECOND = 30
const DEFAULT_FRAME_MS = 1000 / 60
// Avoid a large jump after a throttled or backgrounded tab resumes.
const MAX_FRAME_MS = 50

type ScrollTarget = { kind: 'window'; view: Window; document: Document } | { kind: 'element'; el: HTMLElement }

function findScrollTarget(start: HTMLElement): ScrollTarget {
  const ownerDocument = start.ownerDocument
  const ownerWindow = ownerDocument.defaultView
  if (!ownerWindow) return { kind: 'element', el: start }
  let cur: HTMLElement | null = start
  while (cur && cur !== ownerDocument.body && cur !== ownerDocument.documentElement) {
    const style = ownerWindow.getComputedStyle(cur)
    const overflowY = style.overflowY
    if (
      (overflowY === 'auto' || overflowY === 'scroll' || overflowY === 'overlay') &&
      cur.scrollHeight > cur.clientHeight
    ) {
      return { kind: 'element', el: cur }
    }
    cur = cur.parentElement
  }
  return { kind: 'window', view: ownerWindow, document: ownerDocument }
}

interface EdgeScrollState {
  rafId: number | null
  velocityPxPerSecond: number
  lastFrameAt: number | null
  target: ScrollTarget
  /** The scroll container is looked up once per gesture: the walk reads every ancestor's computed style. */
  found: boolean
  view: Window
}

const sessions = new WeakMap<EditorView, EdgeScrollState>()

function getSession(view: EditorView): EdgeScrollState {
  let session = sessions.get(view)
  if (!session) {
    const ownerDocument = view.dom.ownerDocument
    const ownerWindow = ownerDocument.defaultView
    if (!ownerWindow) throw new Error('The editor document is not attached to a window.')
    session = {
      rafId: null,
      velocityPxPerSecond: 0,
      lastFrameAt: null,
      target: { kind: 'window', view: ownerWindow, document: ownerDocument },
      found: false,
      view: ownerWindow,
    }
    sessions.set(view, session)
  }
  return session
}

/** Ends the gesture's edge scrolling; the next gesture looks its scroll container up again. */
export function stopEdgeScroll(view: EditorView) {
  const edgeScroll = sessions.get(view)
  if (!edgeScroll) return
  halt(edgeScroll)
  edgeScroll.found = false
}

function halt(edgeScroll: EdgeScrollState) {
  if (edgeScroll.rafId !== null) {
    edgeScroll.view.cancelAnimationFrame(edgeScroll.rafId)
    edgeScroll.rafId = null
  }
  edgeScroll.velocityPxPerSecond = 0
  edgeScroll.lastFrameAt = null
}

function runEdgeScrollLoop(edgeScroll: EdgeScrollState) {
  if (edgeScroll.rafId !== null) return
  const tick = (timestamp: number) => {
    if (edgeScroll.velocityPxPerSecond === 0) {
      edgeScroll.rafId = null
      edgeScroll.lastFrameAt = null
      return
    }
    const elapsed = edgeScroll.lastFrameAt === null
      ? DEFAULT_FRAME_MS
      : Math.min(MAX_FRAME_MS, Math.max(0, timestamp - edgeScroll.lastFrameAt))
    edgeScroll.lastFrameAt = timestamp
    const distance = edgeScroll.velocityPxPerSecond * elapsed / 1000
    if (edgeScroll.target.kind === 'window') {
      edgeScroll.target.view.scrollBy(0, distance)
    } else {
      edgeScroll.target.el.scrollTop += distance
    }
    edgeScroll.rafId = edgeScroll.view.requestAnimationFrame(tick)
  }
  edgeScroll.rafId = edgeScroll.view.requestAnimationFrame(tick)
}

export function updateEdgeScroll(view: EditorView, clientY: number) {
  const edgeScroll = getSession(view)
  if (!edgeScroll.found) { edgeScroll.target = findScrollTarget(view.dom); edgeScroll.found = true }
  const target = edgeScroll.target
  edgeScroll.view = view.dom.ownerDocument.defaultView ?? edgeScroll.view

  let topEdge: number
  let bottomEdge: number
  let canScrollUp: boolean
  let canScrollDown: boolean

  if (target.kind === 'window') {
    topEdge = 0
    bottomEdge = target.view.innerHeight
    canScrollUp = target.view.scrollY > 0
    const docEl = target.document.scrollingElement || target.document.documentElement
    canScrollDown = target.view.scrollY + target.view.innerHeight < docEl.scrollHeight
  } else {
    const rect = target.el.getBoundingClientRect()
    topEdge = rect.top
    bottomEdge = rect.bottom
    canScrollUp = target.el.scrollTop > 0
    canScrollDown = target.el.scrollTop + target.el.clientHeight < target.el.scrollHeight
  }

  const distFromTop = clientY - topEdge
  const distFromBottom = bottomEdge - clientY

  let velocity = 0
  if (canScrollUp && distFromTop >= 0 && distFromTop < EDGE_THRESHOLD_PX) {
    const ramp = 1 - distFromTop / EDGE_THRESHOLD_PX
    velocity = -Math.max(EDGE_MIN_VELOCITY_PX_PER_SECOND, ramp * EDGE_MAX_VELOCITY_PX_PER_SECOND)
  } else if (canScrollDown && distFromBottom >= 0 && distFromBottom < EDGE_THRESHOLD_PX) {
    const ramp = 1 - distFromBottom / EDGE_THRESHOLD_PX
    velocity = Math.max(EDGE_MIN_VELOCITY_PX_PER_SECOND, ramp * EDGE_MAX_VELOCITY_PX_PER_SECOND)
  }

  edgeScroll.velocityPxPerSecond = velocity
  if (velocity !== 0) runEdgeScrollLoop(edgeScroll)
  else halt(edgeScroll)
}
