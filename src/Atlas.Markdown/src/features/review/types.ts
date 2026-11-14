import type { MarkdownRichValue } from '../../core/types'
import type { MarkdownHistorySource } from '../history/types'

export interface MarkdownReviewSection {
  id: string
  revision: string
  approved: MarkdownRichValue | null
  working: MarkdownRichValue
  author: string
  status: 'approved' | 'draft' | 'pending'
  deleted: boolean
}
export interface MarkdownSectionDocument {
  revision: string
  sections: readonly MarkdownReviewSection[]
  approvedOrder: readonly string[]
}
export interface MarkdownReviewAccess { canRead: boolean; canContribute: boolean; canEdit: boolean; canReview: boolean }
export type MarkdownSectionDecision = 'approve' | 'reject' | 'withdraw' | 'delete'
/** Host-owned adapter. The server MUST enforce permissions and compare expectedRevision atomically.
 * Read and mutation responses contain detached snapshots. No UI-only permission enforcement. */
export interface MarkdownSectionReviewSource {
  history: MarkdownHistorySource
  read(signal: AbortSignal): Promise<{ document: MarkdownSectionDocument; access: MarkdownReviewAccess }>
  save(id: string, expectedRevision: string, value: MarkdownRichValue, ready: boolean, signal: AbortSignal): Promise<MarkdownSectionDocument>
  add(id: string, after: string | null, value: MarkdownRichValue, signal: AbortSignal): Promise<MarkdownSectionDocument>
  decide(id: string, expectedRevision: string, decision: MarkdownSectionDecision, signal: AbortSignal): Promise<MarkdownSectionDocument>
  /** Push channel owned by the host. Call onChange(revision) after any committed change of the document and
   * onChange(null) whenever intermediate changes may have been missed: after every successful subscription,
   * including the very first one (joining the group is asynchronous and may finish after read() returned), and
   * after every reconnect. Stop when signal aborts. The composable never polls; a host without push must still
   * implement this (for example with SignalR). */
  watch(onChange: (revision: string | null) => void, signal: AbortSignal): void
}
export function canEditSection(section: MarkdownReviewSection, actor: string, access: MarkdownReviewAccess) {
  return access.canRead && (access.canEdit || access.canContribute && (section.status === 'approved' || section.author === actor))
}
export function sectionActions(section: MarkdownReviewSection, actor: string, access: MarkdownReviewAccess): MarkdownSectionDecision[] {
  if (!access.canRead) return []
  const actions: MarkdownSectionDecision[] = []
  if (access.canReview && section.status === 'pending') actions.push('approve')
  if (access.canReview && section.status !== 'approved') actions.push('reject')
  if (section.status !== 'approved' && (access.canEdit || access.canContribute && section.author === actor)) actions.push('withdraw')
  if (!section.deleted && canEditSection(section, actor, access)) actions.push('delete')
  return actions
}
