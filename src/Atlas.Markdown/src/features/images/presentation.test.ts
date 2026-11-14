// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import type { JSONContent } from '@tiptap/core'
import { imagePresentation, imageGeometry, imageViewportStyle, imageCropStyle, imageStyle } from './presentation'
import { createMarkdownEditorController, type MarkdownRichValue } from '../../core'

describe('rich image presentation', () => {
  it('validates bounded edits and refuses a crop that removes the image', () => {
    expect(imagePresentation(null).width).toBe(100)
    expect(() => imagePresentation({ brightness: Infinity })).toThrow()
    expect(() => imagePresentation({ cropLeft: 60, cropRight: 40 })).toThrow()
    expect(() => imagePresentation({ width: '100' })).toThrow()
    expect(() => imagePresentation({ rotate: 45 })).toThrow()
    expect(() => imagePresentation({ naturalWidth: 100 })).toThrow()
    expect(() => imagePresentation({ naturalWidth: -1, naturalHeight: 10 })).toThrow()
    expect(() => imagePresentation({ naturalWidth: 10.1, naturalHeight: 10 })).toThrow()
  })
  it('reserves the actual cropped and rotated footprint without clip-path margins', () => {
    const p = imagePresentation({ naturalWidth: 1200, naturalHeight: 800, cropLeft: 10, cropRight: 15, cropTop: 25, cropBottom: 0, rotate: 90, flip: true, width: 60 })
    expect(imageGeometry(p)).toEqual({ sourceX: 120, sourceY: 200, cropWidth: 900, cropHeight: 600, outputWidth: 600, outputHeight: 900 })
    expect(imageViewportStyle(p)).toContain('aspect-ratio:600/900')
    expect(imageViewportStyle(p)).toContain('width:60%')
    expect(imageCropStyle(p)).toContain('width:150%')
    expect(imageCropStyle(p)).toContain('rotate(90deg) scaleX(-1)')
    expect(imageStyle(p)).toContain('left:-13.333333333333334%')
    expect(imageStyle(p)).not.toContain('clip-path')
  })
  it('preserves fractional source crop geometry and backwards-compatible unloaded defaults', () => {
    const p = imagePresentation({ naturalWidth: 101, naturalHeight: 53, cropLeft: 10, rotate: 270 })
    expect(imageGeometry(p)?.cropWidth).toBeCloseTo(90.9)
    expect(imageGeometry(p)?.outputHeight).toBeCloseTo(90.9)
    expect(imageGeometry(imagePresentation(null))).toBeNull()
    expect(imageStyle(imagePresentation(null))).toContain('height:auto')
  })
  it('preserves original image and presentation in rich roundtrip and reports lossy Markdown', () => {
    const value: MarkdownRichValue = { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [
      { type: 'image', attrs: { src: '/original.png', alt: 'Originál', presentation: { ...imagePresentation({ width: 50, caption: 'Popisek', filter: 'sepia' }) } } },
    ] } }
    const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value } })
    const host = document.createElement('div'); document.body.append(host)
    try {
      controller.attachView(host, { ariaLabel: 'Obrázek' })
      expect(host.querySelector<HTMLElement>('.md-image-viewport')?.style.width).toBe('50%')
      expect(host.querySelector('figcaption')?.textContent).toBe('Popisek')
      const result = controller.getValue(); expect((result.document as JSONContent).content?.[0]?.attrs?.src).toBe('/original.png')
      expect(controller.getProjection()).toMatchObject({ markdownLossy: true })
      expect(controller.getProjection().markdown).toContain('/original.png')
      controller.setValue(result)
      expect(controller.getValue()).toEqual(result)
    } finally { controller.destroy(); host.remove() }
  })
})
