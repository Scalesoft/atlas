import { Extension } from '@tiptap/core'
import { Plugin } from '@tiptap/pm/state'
import { defineMarkdownBlockModule, registerMarkdownFileDropHandler, type MarkdownPluginCommand } from '../../core'
import { attachmentsStrings } from './strings'
import { attachmentSize, normalizeAttachment, type AttachmentData, type AttachmentsPluginOptions } from './data'

function definition(options: AttachmentsPluginOptions = {}, insertCommand?: MarkdownPluginCommand<AttachmentData>) {
  // The stand-in: a file's row (132 px), or without a file the larger picker with its drop hint (223 px drawn).
  return defineMarkdownBlockModule<AttachmentData>({ id: 'attachments', name: 'attachment', openOnInsert: true, stubHeight: data => data.url ? 132 : 223, dataValidator: 'attachment', get label() { return attachmentsStrings.label }, get description() { return attachmentsStrings.slashDescription }, icon: 'paperclip', keywords: ['priloha', 'soubor', 'attachment', 'pdf'],
    insertCommand, defaults: { version: 1, get name() { return attachmentsStrings.newAttachmentName }, url: '' }, normalize: normalizeAttachment,
    text: data => [data.name, data.size === undefined ? '' : attachmentSize(data.size), data.description, data.url].filter(Boolean).join(' · '),
    html: data => ['div', {}, ['strong', {}, data.name], ['p', {}, [data.size === undefined ? '' : attachmentSize(data.size), data.description].filter(Boolean).join(' · ')], ...(data.url ? [['a', { href: data.url, download: data.name }, attachmentsStrings.downloadAttachment]] : [])] as import('@tiptap/pm/model').DOMOutputSpec,
    load: async () => (await import('./runtime')).createAttachmentsRuntime(options),
    extensions: options.upload ? [Extension.create({ name: 'attachmentFileDrop', addProseMirrorPlugins() {
      const editor = this.editor
      return [new Plugin({ view() {
        const destroy = registerMarkdownFileDropHandler(editor, { id: 'attachments', accept: () => true,
          async resolve(file, context) {
            const uploaded = await options.upload!.upload(file, context)
            const data = normalizeAttachment({ size: file.size, mimeType: file.type || undefined, ...uploaded, version: 1 })
            if (!data.url) throw new Error(attachmentsStrings.uploadMissingUrl)
            return { type: 'attachment', attrs: { data } }
          },
        })
        return { destroy }
      } })]
    } })] : [],
  })
}
const module = definition()
export const insertAttachment = module.insert
export const attachmentNode = module.node
export const createAttachmentsPlugin = (options: AttachmentsPluginOptions = {}) => definition(options, module.insert).createPlugin()
export { normalizeAttachment, attachmentSize } from './data'
export type { AttachmentData, AttachmentUploadService, AttachmentsPluginOptions } from './data'
