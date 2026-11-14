// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import { useCollab } from './useCollab'

describe('useCollab', () => {
  it('gives a reader of a live page presence without carets', () => {
    const options = { documentId: 'wiki:reader', user: { id: 'u1', name: 'Reader' }, hubUrl: 'http://localhost/hubs/markdown' }
    const names = (reader: boolean) => {
      const session = useCollab({ ...options, reader })
      const result = session.extensions.map(extension => extension.name)
      session.destroy()
      return result
    }
    expect(names(false)).toContain('collaborationCaret')
    expect(names(true)).not.toContain('collaborationCaret')
  })
})
