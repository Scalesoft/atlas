import { Extension, type Editor } from '@tiptap/core'
import type { Node } from '@tiptap/pm/model'
import { Plugin } from '@tiptap/pm/state'
import { MAX_MARKDOWN_TEXT_LENGTH } from '../../core/documentConstraints'
import { imagesStrings } from '../../ui/strings/images'
import { defineMarkdownAttributeValidator } from '../../schema/constraints'

export interface MarkdownResourcePolicy {
  /** Omitted byte/count/pixel limits preserve existing unbounded behavior. */
  readonly maxFiles?: number
  readonly maxTotalFileBytes?: number
  /** File metadata preflight, rich image view after load, and edited export; not a decoder sandbox. */
  readonly maxImagePixels?: number
  readonly maxCanvasPixels?: number
  readonly imageSources?: {
    readonly allowRelative?: boolean
    readonly allowedOrigins?: readonly string[]
    readonly allowDataImages?: boolean
    readonly allowBlobImages?: boolean
  }
}

export function normalizeResourcePolicy(input: MarkdownResourcePolicy = {}): Readonly<MarkdownResourcePolicy> {
  const result: MarkdownResourcePolicy = { ...input, ...(input.imageSources ? { imageSources: { ...input.imageSources } } : {}) }
  for (const key of ['maxFiles', 'maxTotalFileBytes', 'maxImagePixels', 'maxCanvasPixels'] as const) {
    const value = result[key]
    if (value !== undefined && (!Number.isSafeInteger(value) || value < 1)) throw new TypeError(`${key} must be a positive safe integer.`)
  }
  if (result.imageSources) {
    for (const key of ['allowRelative', 'allowDataImages', 'allowBlobImages'] as const) {
      if (result.imageSources[key] !== undefined && typeof result.imageSources[key] !== 'boolean') throw new TypeError(`${key} must be a boolean.`)
    }
    const origins = result.imageSources.allowedOrigins
    if (origins !== undefined && (!Array.isArray(origins) || origins.some(value => {
      try { const url = new URL(value); return !['http:', 'https:'].includes(url.protocol) || url.origin !== value || !!url.username || !!url.password }
      catch { return true }
    }))) throw new TypeError('Image origins must be canonical HTTP(S) origins.')
    if (origins) (result.imageSources as { allowedOrigins?: readonly string[] }).allowedOrigins = Object.freeze([...origins])
    Object.freeze(result.imageSources)
  }
  return Object.freeze(result)
}

/** A URL policy, not fetch/SSRF protection. Never downloads to validate a source. */
export function imageSource(value: unknown, policy: MarkdownResourcePolicy = {}): string {
  if (value === null || value === undefined || value === '') return '' // Empty image placeholder.
  if (typeof value !== 'string' || !value.trim() || /[\u0000-\u001f\u007f\\]/.test(value)) throw new TypeError(imagesStrings.invalidImageAddress)
  const source = value.trim(), rules = policy.imageSources
  if (/^data:/i.test(source)) {
    if (rules?.allowDataImages === false || !/^data:image\/(?:png|jpeg|gif|webp|avif);base64,[a-z0-9+/=\s]+$/i.test(source)) throw new TypeError(imagesStrings.dataSourceNotAllowed)
    return source
  }
  if (/^blob:/i.test(source)) {
    if (rules?.allowBlobImages === false) throw new TypeError(imagesStrings.blobSourceNotAllowed)
    const url = new URL(source)
    if (url.origin === 'null' || (rules?.allowedOrigins && !rules.allowedOrigins.includes(url.origin))) throw new TypeError(imagesStrings.originNotAllowed)
    return source
  }
  const absolute = /^[a-z][a-z\d+.-]*:/i.test(source) || source.startsWith('//')
  if (source.startsWith('//') && rules?.allowedOrigins) throw new TypeError(imagesStrings.protocolRelativeNotAllowed)
  const url = new URL(source, 'https://editor.invalid/')
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) throw new TypeError(imagesStrings.httpRequired)
  if (!absolute && rules?.allowRelative === false) throw new TypeError(imagesStrings.relativeNotAllowed)
  if (absolute && rules?.allowedOrigins && !rules.allowedOrigins.includes(url.origin)) throw new TypeError(imagesStrings.originNotAllowed)
  return source
}

export function resourcePolicyFor(editor: Editor): Readonly<MarkdownResourcePolicy> { return editor.storage?.markdownResourcePolicy ?? {} }

export function checkFileBudget(files: readonly { size: number; name?: string; type?: string }[], policy: MarkdownResourcePolicy): void {
  if (files.length > (policy.maxFiles ?? Infinity)) throw new RangeError(imagesStrings.tooManyFiles)
  let total = 0
  for (const file of files) {
    if ((/\.(?:md|markdown|txt|mmd|mermaid)$/i.test(file.name ?? '') || /^text\/(?:plain|markdown)$/.test(file.type ?? ''))
      && file.size > MAX_MARKDOWN_TEXT_LENGTH) throw new RangeError(imagesStrings.textFileTooLarge)
    if (!Number.isSafeInteger(file.size) || file.size < 0 || !Number.isSafeInteger(total += file.size)) throw new RangeError(imagesStrings.invalidFileSize)
  }
  if (total > (policy.maxTotalFileBytes ?? Infinity)) throw new RangeError(imagesStrings.totalSizeExceeded)
}

export function checkPixelBudget(width: number, height: number, maximum: number | undefined): void {
  const pixels = width * height
  if (!Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0 || !Number.isFinite(pixels)
    || pixels > (maximum ?? Infinity)) throw new RangeError(imagesStrings.dimensionsExceedLimit)
}

/** Applied to the image node's own attribute: Tiptap local attrs override global attrs. */
export function imageSourceAttribute(policy: MarkdownResourcePolicy) {
  return {
    validate: defineMarkdownAttributeValidator({ kind: 'url', policy: 'image', nullable: true, imageSources: policy.imageSources ?? {} }, value => { imageSource(value, policy) }),
    renderHTML(attrs: Record<string, unknown>) { const src = imageSource(attrs.src, policy); return src ? { src } : {} },
  }
}

declare module '@tiptap/core' { interface Storage { markdownResourcePolicy: Readonly<MarkdownResourcePolicy> } }

export function createResourcePolicyExtension(input?: MarkdownResourcePolicy) {
  const policy = normalizeResourcePolicy(input)
  return Extension.create({
    name: 'markdownResourcePolicy',
    addStorage: () => policy,
    addProseMirrorPlugins: () => [new Plugin({ filterTransaction(transaction) {
      if (!transaction.docChanged) return true
      // Only the top-level blocks the transaction made: the others are the same immutable nodes this filter (or the load check) let in.
      const kept = new Set<Node>()
      transaction.before.forEach(block => { kept.add(block) })
      const check = (node: Node) => { if (node.type.name === 'image') imageSource(node.attrs.src, policy) }
      try {
        transaction.doc.forEach(block => { if (!kept.has(block)) { check(block); block.descendants(check) } })
        return true
      } catch { return false }
    } })],
  })
}
