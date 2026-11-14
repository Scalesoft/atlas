// @vitest-environment jsdom
import { expect, it, vi } from 'vitest'
import { pickYouTrackIssue } from './picker'

it('does not open a late picker when cancelled while its UI module loads', async () => {
  const lifetime = new AbortController()
  const search = vi.fn(async () => [])
  const result = pickYouTrackIssue({ client: { search }, signal: lifetime.signal, ownerDocument: document })
  try {
    expect(document.querySelector('dialog')).toBeNull()
  } finally { lifetime.abort() }
  expect(await result).toBeNull()
  expect(document.querySelector('dialog')).toBeNull()
  expect(search).not.toHaveBeenCalled()
})
