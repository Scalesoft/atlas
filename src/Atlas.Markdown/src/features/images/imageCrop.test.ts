import { describe, expect, it } from 'vitest'
import { imageCropInsets, moveImageCropCorner } from './imageCrop'
import { imagePresentation } from './presentation'

describe('screen-space crop handles', () => {
  for (const rotate of [0, 90, 180, 270]) for (const flip of [false, true]) {
    it(`moves the displayed corner correctly at ${rotate} degrees, flip ${flip}`, () => {
      const initial = imagePresentation({ rotate, flip, cropLeft: 10, cropTop: 20, cropRight: 30, cropBottom: 15 })
      const before = imageCropInsets(initial)
      const next = moveImageCropCorner(initial, 'top-left', 3, 4)
      expect(imageCropInsets(next)).toEqual({ ...before, left: before.left + 3, top: before.top + 4 })
      expect(() => imagePresentation(next)).not.toThrow()
      const shrink = moveImageCropCorner(next, 'bottom-right', -2, -5)
      expect(imageCropInsets(shrink)).toEqual({ left: before.left + 3, top: before.top + 4, right: before.right + 2, bottom: before.bottom + 5 })
    })
  }
  it('clamps overdrag to valid source edges and minimum remaining image', () => {
    const initial = imagePresentation({ cropRight: 30, cropBottom: 20 })
    expect(moveImageCropCorner(initial, 'top-left', 1000, 1000)).toMatchObject({ cropLeft: 64, cropTop: 74 })
    expect(moveImageCropCorner(initial, 'top-left', -1000, -1000)).toMatchObject({ cropLeft: 0, cropTop: 0 })
  })
})
