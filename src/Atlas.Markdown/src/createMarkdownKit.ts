import { createMarkdownContentKit, type MarkdownContentKitOptions } from './core/kit'
import { DragPreview } from './extensions/DragPreview'
import { TableControls } from './extensions/TableControls'
import { SlashCommands, slashSuggestion, type SlashCommandItem } from './extensions/SlashCommands'

export type { MarkdownKit, MarkdownProfile } from './core/kit'
export interface MarkdownKitOptions extends MarkdownContentKitOptions {
  imageSlashItem?: () => SlashCommandItem | null
  slashSuggestionExtra?: SlashCommandItem[]
  compact?: boolean
}

/** Legacy/advanced entry: content schema plus the ready-made UI plugins. */
export function createMarkdownKit(options: MarkdownKitOptions = {}) {
  return createMarkdownContentKit({
    ...options,
    extraExtensions: [
      DragPreview,
      TableControls.configure({ richTableFeatures: options.profile === 'rich' }),
      SlashCommands.configure({
        suggestion: slashSuggestion({
          withImage: options.imageSlashItem,
          extra: options.slashSuggestionExtra ?? [],
          compact: options.compact ?? false,
        }),
      }),
      ...(options.extraExtensions ?? []),
    ],
  })
}
