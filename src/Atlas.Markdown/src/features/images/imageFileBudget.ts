import { checkPixelBudget, type MarkdownResourcePolicy } from './resourcePolicy'
import { imagesStrings } from '../../ui/strings/images'

/** Reads at most 256 KiB of metadata; does not invoke the browser image decoder. */
export async function checkImageFileBudget(file: File, policy: MarkdownResourcePolicy, signal: AbortSignal): Promise<void> {
  if (policy.maxImagePixels === undefined) return
  if (signal.aborted) throw new DOMException(imagesStrings.importCancelled, 'AbortError')
  const bytes = new Uint8Array(await file.slice(0, 256 * 1024).arrayBuffer())
  if (signal.aborted) throw new DOMException(imagesStrings.importCancelled, 'AbortError')
  const dimensions = imageHeaderDimensions(bytes)
  if (!dimensions) throw new RangeError(imagesStrings.invalidImageDimensions)
  checkPixelBudget(dimensions.width, dimensions.height, policy.maxImagePixels)
}

/** The pixel size an image file declares in its header (no decoding), or null when it is not one this reads. */
export async function imageFileDimensions(file: File): Promise<{ width: number; height: number } | null> {
  return imageHeaderDimensions(new Uint8Array(await file.slice(0, 256 * 1024).arrayBuffer()))
}

export function imageHeaderDimensions(bytes: Uint8Array): { width: number; height: number } | null {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
  const text = (at: number, length: number) => String.fromCharCode(...bytes.subarray(at, at + length))
  if (bytes.length >= 24 && bytes[0] === 137 && text(1, 7) === 'PNG\r\n\x1a\n' && text(12, 4) === 'IHDR') {
    return { width: view.getUint32(16), height: view.getUint32(20) }
  }
  if (bytes.length >= 10 && ['GIF87a', 'GIF89a'].includes(text(0, 6))) {
    return { width: view.getUint16(6, true), height: view.getUint16(8, true) }
  }
  if (bytes.length >= 30 && text(0, 4) === 'RIFF' && text(8, 4) === 'WEBP') {
    const kind = text(12, 4)
    if (kind === 'VP8X') return {
      width: 1 + bytes[24]! + (bytes[25]! << 8) + (bytes[26]! << 16),
      height: 1 + bytes[27]! + (bytes[28]! << 8) + (bytes[29]! << 16),
    }
    if (kind === 'VP8 ' && bytes[23] === 0x9d && bytes[24] === 1 && bytes[25] === 0x2a) {
      return { width: view.getUint16(26, true) & 0x3fff, height: view.getUint16(28, true) & 0x3fff }
    }
  }
  if (bytes.length >= 25 && text(0, 4) === 'RIFF' && text(8, 4) === 'WEBP' && text(12, 4) === 'VP8L' && bytes[20] === 0x2f) {
    const bits = view.getUint32(21, true)
    return { width: 1 + (bits & 0x3fff), height: 1 + ((bits >>> 14) & 0x3fff) }
  }
  if (bytes[0] === 0xff && bytes[1] === 0xd8) {
    let offset = 2
    while (offset + 4 <= bytes.length) {
      if (bytes[offset++] !== 0xff) return null
      while (bytes[offset] === 0xff) offset++
      const marker = bytes[offset++]
      if (marker === undefined || marker === 0xda || marker === 0xd9) return null
      if (marker === 1 || marker >= 0xd0 && marker <= 0xd7) continue
      if (offset + 2 > bytes.length) return null
      const size = view.getUint16(offset)
      if (size < 2 || offset + size > bytes.length) return null
      if ([0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf].includes(marker)) {
        return size >= 8 ? { width: view.getUint16(offset + 5), height: view.getUint16(offset + 3) } : null
      }
      offset += size
    }
  }
  return null
}
