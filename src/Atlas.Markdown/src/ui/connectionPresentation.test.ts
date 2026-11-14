import { describe, expect, it } from 'vitest'
import type { MarkdownEditorState } from '../core/types'
import { connectionPresentation, WEAK_LATENCY_MS } from './connectionPresentation'

const online: MarkdownEditorState = {
  contentRevision: 3, changeOrigin: 'local', lifecycle: 'ready', binding: 'collaborative',
  editable: true, focused: false, connection: 'online', documentSave: 'saved',
  projectionSave: 'current', pendingChanges: 0, error: null, peers: [],
  link: { latencyMs: 40, drops: 0, stalled: false },
}

it('reports a healthy link as the calm level with a full meter and no card', () => {
  const presentation = connectionPresentation(online)
  expect(presentation.level).toBe('live')
  expect(presentation.bars).toBe(3)
  expect(presentation.card).toBe(false)
  expect(presentation.busy).toBe(false)
})

it('never offers a card or a meter for a local document', () => {
  const presentation = connectionPresentation({ ...online, binding: 'local', connection: 'local', link: null })
  expect(presentation.level).toBe('local')
  expect(presentation.card).toBe(false)
})

describe('degraded but connected', () => {
  it('drops to weak once acknowledgements take longer than the healthy threshold', () => {
    expect(connectionPresentation({ ...online, link: { latencyMs: WEAK_LATENCY_MS + 1, drops: 0, stalled: false } }).level).toBe('weak')
    expect(connectionPresentation({ ...online, link: { latencyMs: WEAK_LATENCY_MS, drops: 0, stalled: false } }).level).toBe('live')
  })

  it('drops to weak while local edits wait for an acknowledgement', () => {
    const presentation = connectionPresentation({ ...online, pendingChanges: 2, link: { latencyMs: 30, drops: 1, stalled: true } })
    expect(presentation.level).toBe('weak')
    expect(presentation.bars).toBe(2)
    expect(presentation.card).toBe(false)
  })

  it('drops to weak when the server cannot store the document', () => {
    expect(connectionPresentation({ ...online, documentSave: 'degraded' }).level).toBe('weak')
    expect(connectionPresentation({ ...online, projectionSave: 'degraded' }).level).toBe('weak')
  })
})

describe('interrupted', () => {
  it('separates the first connect from a lost session so the copy can differ', () => {
    expect(connectionPresentation({ ...online, lifecycle: 'starting', connection: 'connecting', editable: false }).level).toBe('connecting')
    expect(connectionPresentation({ ...online, connection: 'reconnecting', editable: false }).level).toBe('reconnecting')
    expect(connectionPresentation({ ...online, connection: 'offline', editable: false }).level).toBe('offline')
  })

  it('treats an online connection that has not hydrated as the first connect', () => {
    expect(connectionPresentation({ ...online, lifecycle: 'starting', editable: false }).level).toBe('connecting')
  })

  it('asks for a card and an empty meter once the session is interrupted', () => {
    const presentation = connectionPresentation({ ...online, connection: 'reconnecting', editable: false })
    expect(presentation.card).toBe(true)
    expect(presentation.busy).toBe(true)
    expect(presentation.bars).toBe(1)
    expect(connectionPresentation({ ...online, connection: 'offline', editable: false }).bars).toBe(0)
  })
})

describe('unrecoverable', () => {
  const failure = { kind: 'collaboration', message: 'Unauthorized', recoverable: false } as const

  it('outranks every other state and is never busy', () => {
    const presentation = connectionPresentation({ ...online, connection: 'failed', lifecycle: 'failed', editable: false, error: failure })
    expect(presentation.level).toBe('failed')
    expect(presentation.bars).toBe(0)
    expect(presentation.card).toBe(true)
    expect(presentation.busy).toBe(false)
  })

  it('reports a failed lifecycle even while the transport still claims to be online', () => {
    expect(connectionPresentation({ ...online, lifecycle: 'failed', error: failure }).level).toBe('failed')
  })
})

it('explains an interruption differently for a host that permits offline editing', () => {
  const paused = connectionPresentation({ ...online, connection: 'offline', editable: false })
  const offlineEditing = connectionPresentation({ ...online, connection: 'offline', editable: true })
  expect(paused.detail).toContain('pozastaven')
  expect(offlineEditing.detail).not.toContain('pozastaven')
  expect(offlineEditing.detail).toContain('bez spojení')
})

it('ranks severity so a caller can tell an escalation from a recovery', () => {
  const rank = (state: MarkdownEditorState) => connectionPresentation(state).severity
  expect(rank(online)).toBeLessThan(rank({ ...online, link: { latencyMs: 4000, drops: 0, stalled: false } }))
  expect(rank({ ...online, link: { latencyMs: 4000, drops: 0, stalled: false } }))
    .toBeLessThan(rank({ ...online, connection: 'reconnecting' }))
  expect(rank({ ...online, connection: 'reconnecting' })).toBeLessThan(rank({ ...online, connection: 'offline' }))
  expect(rank({ ...online, connection: 'offline' })).toBeLessThan(rank({ ...online, lifecycle: 'failed' }))
})
