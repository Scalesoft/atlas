import { describe, expect, it } from 'vitest'
import { createSignalRConnection } from './transport'

// The client drops the connection when it hears nothing for `serverTimeout`. What it hears in a
// quiet period are the SERVER's keepalive pings (ASP.NET Core default every 15 s), so the timeout
// must cover at least two of them; the old 6 s value disconnected on every idle stretch.
function hubOf(connection: unknown): { serverTimeoutInMilliseconds: number; keepAliveIntervalInMilliseconds: number } {
  return (connection as { connection: { serverTimeoutInMilliseconds: number; keepAliveIntervalInMilliseconds: number } }).connection
}

describe('createSignalRConnection', () => {
  it('defaults to a timeout that survives two server keepalives (>= 30 s)', () => {
    const hub = hubOf(createSignalRConnection('https://example.test/hubs/markdown'))
    expect(hub.serverTimeoutInMilliseconds).toBeGreaterThanOrEqual(30_000)
    expect(hub.keepAliveIntervalInMilliseconds).toBe(15_000)
  })

  it('accepts a shorter timeout only when the host declares a faster server keepalive', () => {
    const hub = hubOf(createSignalRConnection('https://example.test/hubs/markdown', undefined, { serverKeepAliveMs: 5_000, serverTimeoutMs: 12_000 }))
    expect(hub.serverTimeoutInMilliseconds).toBe(12_000)
  })

  it('rejects the old 6 s timeout against the default 15 s server keepalive', () => {
    expect(() => createSignalRConnection('https://example.test/hubs/markdown', undefined, { keepAliveIntervalMs: 2_000, serverTimeoutMs: 6_000 }))
      .toThrow(TypeError)
  })

  it('rejects a timeout shorter than twice a declared server keepalive', () => {
    expect(() => createSignalRConnection('https://example.test/hubs/markdown', undefined, { serverKeepAliveMs: 10_000, serverTimeoutMs: 15_000 }))
      .toThrow(TypeError)
  })
})
