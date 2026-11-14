import { describe, expect, it } from 'vitest'
import { normalizeAttachment } from './data'
describe('attachment validation', () => {
  it('accepts a persisted upload and zero-byte files', () => {
    expect(normalizeAttachment({ version: 1, name: ' Empty.txt ', url: '/files/1', size: 0, mimeType: 'text/plain' })).toEqual({ version: 1, name: 'Empty.txt', url: '/files/1', size: 0, mimeType: 'text/plain' })
  })
  it('rejects executable URLs, unsafe metadata, unsupported versions and invalid sizes', () => {
    const valid = { version: 1, name: 'test.pdf', url: 'https://example.cz/test.pdf' }
    for (const patch of [{ url: 'javascript:alert(1)' }, { url: 'data:text/html,x' }, { version: 2 }, { name: 'x\r\n.exe' }, { size: NaN }, { size: Infinity }, { size: -1 }, { size: 1.5 }, { size: '42' }, { mimeType: 'text/plain\nX-Test: a' }]) expect(() => normalizeAttachment({ ...valid, ...patch })).toThrow()
  })
})
