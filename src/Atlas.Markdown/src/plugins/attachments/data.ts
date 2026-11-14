import { markdownModuleUrl, markdownStrings } from '../../core'
import { attachmentsStrings } from './strings'

export interface AttachmentData { version: 1; name: string; url: string; size?: number; mimeType?: string; description?: string }
export interface AttachmentUploadService {
  upload(file: File, context: { signal: AbortSignal }): Promise<Omit<AttachmentData, 'version'>>
}
export interface AttachmentsPluginOptions { upload?: AttachmentUploadService }
export function normalizeAttachment(value: unknown): AttachmentData {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new TypeError(attachmentsStrings.invalidAttachment)
  const v = value as Record<string, unknown>
  if (v.version !== 1 || typeof v.name !== 'string' || !v.name.trim() || v.name.length > 512 || /[\u0000-\u001f\u007f]/u.test(v.name)) throw new TypeError(attachmentsStrings.invalidNameOrVersion)
  if (typeof v.url !== 'string' || v.url.length > 8192 || /[\r\n]/u.test(v.url)) throw new TypeError(attachmentsStrings.invalidUrl)
  const result: AttachmentData = { version: 1, name: v.name.trim(), url: markdownModuleUrl(v.url, true) }
  if (v.size !== undefined) {
    if (typeof v.size !== 'number' || !Number.isSafeInteger(v.size) || v.size < 0) throw new TypeError(attachmentsStrings.invalidSize)
    result.size = v.size
  }
  if (v.mimeType !== undefined && v.mimeType !== '') {
    if (typeof v.mimeType !== 'string' || !/^[a-z0-9!#$&^_.+-]+\/[a-z0-9!#$&^_.+-]+$/i.test(v.mimeType)) throw new TypeError(attachmentsStrings.invalidMimeType)
    result.mimeType = v.mimeType
  }
  if (v.description !== undefined) {
    if (typeof v.description !== 'string' || v.description.length > 4096 || /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/u.test(v.description)) throw new TypeError(attachmentsStrings.invalidDescription)
    result.description = v.description
  }
  return result
}
export function attachmentSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  const exponent = Math.min(3, Math.floor(Math.log(bytes) / Math.log(1024)))
  return `${new Intl.NumberFormat(markdownStrings.locale, { maximumFractionDigits: 1 }).format(bytes / 1024 ** exponent)} ${['B', 'KiB', 'MiB', 'GiB'][exponent]}`
}
