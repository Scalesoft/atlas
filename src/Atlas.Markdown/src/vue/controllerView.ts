import { Extension } from '@tiptap/core'
import type { MarkdownEditorController } from '../core'
import { configureControllerView, editorForController } from '../core/internal'
import { DragPreview } from '../extensions/DragPreview'
import { TableControls } from '../extensions/TableControls'
import { closeSlashSuggestion, SlashCommands, slashSuggestion, type SlashCommandItem } from '../extensions/SlashCommands'
import { KeyboardFirst, KeyboardFirstEnter } from '../extensions/KeyboardFirst'
import { CaretBlock } from '../extensions/CaretBlock'
import { SmoothCaret } from '../extensions/SmoothCaret'
import { setImageUploadService } from '../extensions/ImageUpload'
import { pickAndUploadImage } from '../features/images/imagePicker'
import type { ImageUploadService } from '../features/images/uploadService'
import { setEditorPlaceholder } from '../features/placeholder/configuration'
import { surfaceStrings } from '../ui/strings/surface'

export interface MarkdownEditorViewOptions { compact: boolean; imageUpload?: ImageUploadService; placeholder?: string; smoothCaret?: boolean }
export interface MarkdownEditorViewConfiguration { update(options: MarkdownEditorViewOptions): void }

/** The ready-made view contributes UI; document features belong to plugins. */
export function configureMarkdownEditorView(controller: MarkdownEditorController, initial: MarkdownEditorViewOptions): MarkdownEditorViewConfiguration {
  const options = { ...initial }
  const extra: SlashCommandItem[] = controller.actions.slash.map(action => ({
    id: action.id, title: action.label, description: action.description ?? '', searchTerms: [...(action.keywords ?? [])],
    icon: action.icon ?? '+', variants: action.variants, commandValue: action.command, parameter: action.parameter,
    command: ({ range }) => { controller.dispatch(action.command, { replaceRange: range }) },
  }))
  configureControllerView(controller, [
    Extension.create({
      name: 'markdownEditorViewConfiguration',
      onBeforeCreate() {
        setImageUploadService(this.editor, options.imageUpload)
        setEditorPlaceholder(this.editor, options.placeholder)
      },
    }),
    DragPreview,
    CaretBlock,
    ...initial.smoothCaret ? [SmoothCaret] : [],
    KeyboardFirst.configure({ compact: () => options.compact }),
    KeyboardFirstEnter,
    TableControls.configure({ richTableFeatures: controller.profile === 'rich' }),
    SlashCommands.configure({ suggestion: slashSuggestion({
      controller, compact: () => options.compact, extra,
      withImage: () => options.imageUpload ? ({
        id: 'image', title: surfaceStrings.imageSlashTitle, description: surfaceStrings.imageSlashDescription, searchTerms: ['image', 'obrazek', 'foto'],
        icon: 'svg:image', variants: ['document'],
        command: ({ editor, range }) => {
          editor.chain().deleteRange(range).run()
          pickAndUploadImage(editor, options.imageUpload!, ['image/png', 'image/jpeg', 'image/gif', 'image/webp'])
        },
      }) : null,
    }) }),
  ])
  return {
    update(next) {
      const compactChanged = options.compact !== next.compact
      const uploadChanged = options.imageUpload !== next.imageUpload
      const placeholderChanged = options.placeholder !== next.placeholder
      options.compact = next.compact
      options.imageUpload = next.imageUpload
      options.placeholder = next.placeholder
      const editor = editorForController(controller)
      if (!editor) return
      if (uploadChanged) setImageUploadService(editor, next.imageUpload)
      if (placeholderChanged) setEditorPlaceholder(editor, next.placeholder, true)
      if (compactChanged || uploadChanged) closeSlashSuggestion(editor)
    },
  }
}
