import { imagesStrings } from '../../ui/strings/images'

export interface UploadedImage {
  url: string
  alt?: string
}

/** Network and authentication belong to the host; the editor owns cancellation. */
export type ImageUploadService = (file: File, context: { signal: AbortSignal }) => Promise<UploadedImage>

export const imageUploadErrorEvent = 'md-image-upload-error'

/** Compatibility adapter for the existing multipart POST endpoint. */
export function createHttpImageUpload(uploadUrl: string): ImageUploadService {
  return async (file, { signal }) => {
    const body = new FormData()
    body.append('file', file)
    const response = await fetch(uploadUrl, { method: 'POST', body, signal })
    if (!response.ok) throw new Error(imagesStrings.httpUploadFailed(response.status))
    const image: unknown = await response.json()
    if (!isUploadedImage(image)) throw new Error(imagesStrings.uploadInvalidUrl)
    return image
  }
}

export function isUploadedImage(value: unknown): value is UploadedImage {
  if (typeof value !== 'object' || value === null) return false
  const image = value as Record<string, unknown>
  return typeof image.url === 'string' && image.url.trim().length > 0
    && (image.alt === undefined || typeof image.alt === 'string')
}
