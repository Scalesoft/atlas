import { imagesStrings } from '../../ui/strings/images'

export interface ImagePresentation {
  width: number; align: 'left' | 'center' | 'right'; caption: string
  rotate: number; flip: boolean; brightness: number; contrast: number; saturation: number
  filter: 'none' | 'grayscale' | 'sepia'; cropLeft: number; cropRight: number; cropTop: number; cropBottom: number
  /** Source dimensions captured by an explicit edit. Zero means an older/unloaded image. */
  naturalWidth: number; naturalHeight: number
}
export const defaultImagePresentation: ImagePresentation = {
  width: 100, align: 'center', caption: '', rotate: 0, flip: false, brightness: 100, contrast: 100, saturation: 100,
  filter: 'none', cropLeft: 0, cropRight: 0, cropTop: 0, cropBottom: 0, naturalWidth: 0, naturalHeight: 0,
}
export function imagePresentation(value: unknown): ImagePresentation {
  if (value == null) return { ...defaultImagePresentation }
  if (typeof value !== 'object' || Array.isArray(value)) throw new TypeError(imagesStrings.invalidPresentation)
  const data = { ...defaultImagePresentation, ...value }
  if (Object.keys(value).some(key => !(key in defaultImagePresentation)) || typeof data.caption !== 'string'
    || typeof data.flip !== 'boolean' || !['left', 'center', 'right'].includes(data.align)
    || !['none', 'grayscale', 'sepia'].includes(data.filter) || ![0, 90, 180, 270].includes(data.rotate)) throw new TypeError(imagesStrings.invalidPresentation)
  for (const key of ['width', 'brightness', 'contrast', 'saturation', 'cropLeft', 'cropRight', 'cropTop', 'cropBottom'] as const) {
    const n = data[key]
    if (!Number.isFinite(n) || n < (key === 'width' ? 10 : 0) || n > (key.startsWith('crop') ? 90 : key === 'width' ? 100 : 200)) throw new TypeError(imagesStrings.adjustmentOutOfRange)
  }
  if (data.cropLeft + data.cropRight >= 95 || data.cropTop + data.cropBottom >= 95) throw new TypeError(imagesStrings.cropMustKeepMinimum)
  if (![data.naturalWidth, data.naturalHeight].every(n => Number.isSafeInteger(n) && n >= 0 && n <= 1000000)
    || (data.naturalWidth === 0) !== (data.naturalHeight === 0)) throw new TypeError(imagesStrings.invalidSourceDimensions)
  return data
}
/** Absent, empty or all-default: what parsing an image from Markdown or plain HTML produces. */
export function isDefaultImagePresentation(value: unknown): boolean {
  if (value == null) return true
  try {
    const p = imagePresentation(value)
    return (Object.keys(defaultImagePresentation) as Array<keyof ImagePresentation>).every(key => p[key] === defaultImagePresentation[key])
  } catch { return false }
}
export function imageFilter(p: ImagePresentation): string {
  return `brightness(${p.brightness}%) contrast(${p.contrast}%) saturate(${p.saturation}%) ${p.filter === 'none' ? '' : `${p.filter}(1)`}`
}
export function imageGeometry(p: ImagePresentation, width = p.naturalWidth, height = p.naturalHeight) {
  if (!width || !height) return null
  const sourceX = width * p.cropLeft / 100; const sourceY = height * p.cropTop / 100
  const cropWidth = width * (1 - (p.cropLeft + p.cropRight) / 100)
  const cropHeight = height * (1 - (p.cropTop + p.cropBottom) / 100)
  const turned = p.rotate % 180 !== 0
  return { sourceX, sourceY, cropWidth, cropHeight, outputWidth: turned ? cropHeight : cropWidth, outputHeight: turned ? cropWidth : cropHeight }
}
export function imageViewportStyle(p: ImagePresentation): string {
  const geometry = imageGeometry(p)
  return `position:relative;overflow:hidden;max-width:100%;width:${p.width}%;display:block;margin-left:${p.align === 'left' ? '0' : 'auto'};margin-right:${p.align === 'right' ? '0' : 'auto'};${geometry ? `aspect-ratio:${geometry.outputWidth}/${geometry.outputHeight};` : ''}`
}
export function imageCropStyle(p: ImagePresentation): string {
  const g = imageGeometry(p)
  if (!g) return 'display:block;position:relative;overflow:hidden;'
  return `position:absolute;overflow:hidden;left:50%;top:50%;width:${g.cropWidth / g.outputWidth * 100}%;height:${g.cropHeight / g.outputHeight * 100}%;transform:translate(-50%,-50%) rotate(${p.rotate}deg) scaleX(${p.flip ? -1 : 1});transform-origin:center;`
}
export function imageStyle(p: ImagePresentation): string {
  const g = imageGeometry(p)
  if (!g) return `width:100%;height:auto;max-width:none;display:block;filter:${imageFilter(p)};`
  return `position:absolute;display:block;max-width:none!important;max-height:none!important;width:${p.naturalWidth / g.cropWidth * 100}%;height:${p.naturalHeight / g.cropHeight * 100}%;left:${-g.sourceX / g.cropWidth * 100}%;top:${-g.sourceY / g.cropHeight * 100}%;margin:0;filter:${imageFilter(p)};`
}
