import type { Editor } from '@tiptap/core'
import { createMarkdownStubbedPopover, type MarkdownPopoverAnchor } from '../../core'
import { youtrackStrings } from './pickerStrings'
import type { YouTrackClient, YouTrackIssue } from './types'

export interface YouTrackPickerContext {
  readonly client: YouTrackClient
  readonly signal: AbortSignal
  readonly ownerDocument: Document
  /** Source of editor theme tokens for the body-level dialog. */
  readonly themeElement?: HTMLElement
  /** Associates a body-level dialog with its editor for fullscreen focus isolation. */
  readonly registerOverlay?: (overlay: HTMLElement) => () => void
  /** Text to search for as soon as the dialog opens (the slash parameter, `/úkol 123`). */
  readonly initialQuery?: string
  /** Enables the shared editor panel; omit to retain the standalone native dialog. */
  readonly editor?: Editor
  readonly anchor?: HTMLElement
  /** Live selection/caret geometry independent of the trigger's lifetime. */
  readonly positionAnchor?: MarkdownPopoverAnchor
  /** Host policy or snapshot explanation. Always rendered as plain text. */
  readonly helpText?: string
  /** The popover the shared panel was opened as already (the stub that waited for this module): the picker draws into it. */
  readonly host?: { readonly container: HTMLElement; readonly close: () => void; /** Set by the picker: what closing the popover must do. */ cleanup?: () => void }
}


/** The anchored picker opens at once as a stub while its module loads (stubbedPopover.ts); prefetched when the slash menu offers it. */
const pickerPopover = createMarkdownStubbedPopover(() => import('./pickerRuntime'))
export const prefetchYouTrackPicker = () => pickerPopover.prefetchWhenIdle()

/** Load the picker UI only when requested; cancelled requests never mount it. */
export async function pickYouTrackIssue(context: YouTrackPickerContext): Promise<YouTrackIssue | null> {
  if (context.signal.aborted) return null
  const { editor, anchor } = context
  if (!editor || !anchor) {
    const { pickYouTrackIssue } = await import('./pickerRuntime')
    return context.signal.aborted ? null : pickYouTrackIssue(context)
  }
  if (editor.isDestroyed || !editor.isEditable) return null
  return new Promise(resolve => {
    pickerPopover.open({ editor, anchor, positionAnchor: context.positionAnchor, title: youtrackStrings.pickerLabel, className: 'md-youtrack-picker',
      trackSelection: false, signal: context.signal, rows: 3, rowHeight: 36, onCancelled: () => resolve(null),
      render: (module, container, close) => {
        const host: NonNullable<YouTrackPickerContext['host']> = { container, close }
        void module.pickYouTrackIssue({ ...context, host }).then(resolve)
        return () => host.cleanup?.()
      } })
  })
}
