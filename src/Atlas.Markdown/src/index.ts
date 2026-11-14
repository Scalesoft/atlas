/** Public surface of the central @atlas/markdown package. */
export * from './vue'
export { createMarkdownEditorController, defineMarkdownCommand, migrateMarkdownValue } from './core'
export type { MarkdownEditorController, LocalMarkdownEditorController, MarkdownCommand, MarkdownValue, MarkdownValueInput, MarkdownRichValue, MarkdownRichInput, MarkdownProfile, MarkdownProjection, MarkdownEditorState, MarkdownDocumentSchema, MarkdownPlugin, MarkdownPluginInstance, MarkdownPluginContext, MarkdownPluginRenderContext, MarkdownReadonlyJSONContent, MarkdownOverlayOptions, MarkdownAction, MarkdownActionParameter, MarkdownPluginPanel, MarkdownPanelPlacement, MarkdownPluginMigration, MarkdownPluginCommand, MarkdownPluginCommandDefinition, MarkdownPluginCommandCall, MarkdownPluginCommandOptions, MarkdownSchemaPlugin, ImageUploadService, UploadedImage } from './core'
export { default as PoolEditor } from './PoolEditor.vue'
export {
  createMarkdownKit,
  type MarkdownKit,
  type MarkdownKitOptions,
} from './createMarkdownKit'
export type { SlashCommandItem } from './extensions/SlashCommands'
export { SlashCommands, slashSuggestion, slashItems } from './extensions/SlashCommands'
export { ImageUpload } from './extensions/ImageUpload'
export type { ImageUploadOptions } from './extensions/ImageUpload'
export { TableControls } from './extensions/TableControls'
export { CellTextAlign } from './extensions/CellTextAlign'
export { CellBackground } from './extensions/CellBackground'
export { RowHeight } from './extensions/RowHeight'
export { DragPreview } from './extensions/DragPreview'
export { createSuggestionPopup } from './extensions/floatingPopup'
export type { SuggestionPopup, SuggestionPopupOptions } from './extensions/floatingPopup'
export * as icons from './ui/icons'
