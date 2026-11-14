import { defineAsyncComponent, h } from 'vue'
import { panelsStrings } from '../ui/strings/panels'
export { default as MarkdownEditor } from './MarkdownEditor.vue'
export { default as MarkdownEditorRoot } from './MarkdownEditorRoot.vue'
export { default as MarkdownEditorContent } from './MarkdownEditorContent.vue'
export { default as MarkdownRenderer } from './MarkdownRenderer.vue'
// A page's history and the section review are views a host opens on purpose, never part of an editor's first render: their
// chunks load when rendered, as the source panel's below, and a failed download says so in place (rendering again retries).
const viewLoadError = (text: () => string) => () => h('p', { class: 'md-view-load-error', role: 'alert' }, text())
export const MarkdownDocumentHistory = defineAsyncComponent({
  loader: () => import('./MarkdownDocumentHistory.vue'),
  errorComponent: viewLoadError(() => panelsStrings.historyLoadError),
})
export { default as MarkdownDetailWorkspace } from './MarkdownDetailWorkspace.vue'
// Tiny and needed synchronously (a draft to resolve must show at once), so it stays eager.
export { default as MarkdownDraftConfirmation } from './MarkdownDraftConfirmation.vue'
// Loaded when a host renders the „Zdroj“ tab: it costs the eager bundle nothing. A failed download
// (offline) says so in place; rendering the panel again retries it.
const SourcePanelLoadError = () => h('p', { class: 'md-source-panel-error', role: 'alert' }, panelsStrings.sourceLoadError)
export const MarkdownSourcePanel = defineAsyncComponent({
  loader: () => import('./MarkdownSourcePanel.vue'),
  errorComponent: SourcePanelLoadError,
})
export { useMarkdownSourceDraft } from './useMarkdownSourceDraft'
export type {
  MarkdownSourceDraft, MarkdownSourceDraftOptions, MarkdownSourceDraftStatus, MarkdownSourceNotice, MarkdownSourceNoticeKind,
  MarkdownSourceSaveContext, MarkdownSourceSaveFailure, MarkdownSourceSaveResult,
} from './useMarkdownSourceDraft'
export const MarkdownSectionReview = defineAsyncComponent({
  loader: () => import('./MarkdownSectionReview.vue'),
  errorComponent: viewLoadError(() => panelsStrings.reviewLoadError),
})
export { useMarkdownEditor } from './useMarkdownEditor'
export type { MarkdownFormattingItem } from '../ui/formatting'
export type { MarkdownOutlineMode, MarkdownOutlineOptions } from '../ui/outline'
