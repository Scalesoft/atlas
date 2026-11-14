import type { MarkdownRichValue } from '../../core/types'

export type MarkdownHistoryKind = 'edit' | 'structure' | 'approve' | 'reject' | 'create' | 'submit'

/** Immutable, document-wide checkpoint. IDs are opaque and unique even at the same timestamp. */
export interface MarkdownHistoryEntry {
  id: string
  timestamp: string
  author: string
  label: string
  kind: MarkdownHistoryKind
}

export interface MarkdownHistoryPage {
  /** Newest first, in commit order. A cursor must remain stable while newer writes arrive. */
  entries: readonly MarkdownHistoryEntry[]
  nextCursor?: string | null
}

export interface MarkdownHistorySnapshot {
  entry: MarkdownHistoryEntry
  /** Published content AT this checkpoint, not today's approved document. */
  approved: MarkdownRichValue
  /** Optional complete unpublished document at the same checkpoint. Never a partial JSON patch. */
  working?: MarkdownRichValue | null
  workingLabel?: string | null
}

/** A host supplies storage and authentication. The editor never assumes Git, HTTP or a user ID. */
export interface MarkdownHistorySource {
  page(request: { cursor?: string; limit: number; signal: AbortSignal }): Promise<MarkdownHistoryPage>
  snapshot(request: { id: string; signal: AbortSignal }): Promise<MarkdownHistorySnapshot>
}
