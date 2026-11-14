// @vitest-environment jsdom

import { afterEach, describe, expect, it, vi } from 'vitest'
import { createHttpImageUpload } from './uploadService'

afterEach(() => vi.unstubAllGlobals())

describe('HTTP image upload compatibility adapter', () => {
  it('posts the file using the existing multipart contract and propagates the abort signal', async () => {
    const fetch = vi.fn(async () => ({ ok: true, json: async () => ({ url: '/image.png', alt: 'A photo' }) }))
    vi.stubGlobal('fetch', fetch)
    const file = new File(['image'], 'photo.png', { type: 'image/png' })
    const signal = new AbortController().signal

    expect(await createHttpImageUpload('/upload')(file, { signal })).toEqual({ url: '/image.png', alt: 'A photo' })
    expect(fetch).toHaveBeenCalledWith('/upload', expect.objectContaining({ method: 'POST', signal }))
    const request = fetch.mock.calls[0] as unknown as [string, RequestInit]
    expect((request[1].body as FormData).get('file')).toBe(file)
  })

  it('rejects failed HTTP responses before attempting to insert a result', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: false, status: 503 })))
    await expect(createHttpImageUpload('/upload')(new File([], 'photo.png'), { signal: new AbortController().signal }))
      .rejects.toThrow('503')
  })

  it.each([null, {}, { url: '' }, { url: ' ' }, { url: 42 }, { url: '/photo.png', alt: 42 }])('rejects malformed response %j', async value => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => value })))
    await expect(createHttpImageUpload('/upload')(new File([], 'photo.png'), { signal: new AbortController().signal }))
      .rejects.toThrow('platnou adresu')
  })
})
