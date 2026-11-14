import type { MarkdownContentStamp, MarkdownDocumentSchema, MarkdownValueSnapshot } from '../core/types'

export interface MarkdownDraftScope {
  readonly serverId: string
  readonly tenantId: string
  readonly accountId: string
  readonly documentId: string
}
export interface MarkdownDraftRecord {
  readonly formatVersion: 1
  readonly scope: MarkdownDraftScope
  readonly branchId: string
  readonly revision: number
  readonly capturedAt: number
  readonly schema: MarkdownDocumentSchema
  readonly snapshot: MarkdownValueSnapshot
}
export interface MarkdownDraftStore {
  write(record: MarkdownDraftRecord, expectedRevision: number): Promise<void>
  read(scope: MarkdownDraftScope, branchId: string): Promise<MarkdownDraftRecord | null>
  list(scope: MarkdownDraftScope): Promise<MarkdownDraftRecord[]>
  discard(scope: MarkdownDraftScope, branchId: string, expectedRevision: number): Promise<void>
  close(): void
}
export type MarkdownDraftErrorCode = 'invalid' | 'unavailable' | 'closed' | 'blocked' | 'storage' | 'quota' | 'conflict' | 'discarded' | 'corrupt'
export class MarkdownDraftError extends Error {
  constructor(readonly code: MarkdownDraftErrorCode, message: string, options?: ErrorOptions) {
    super(message, options); this.name = 'MarkdownDraftError'
  }
}
export interface MarkdownDraftState {
  readonly branchId: string
  readonly status: 'pending' | 'persisted' | 'failed' | 'closed'
  readonly current: MarkdownContentStamp
  readonly persisted: MarkdownContentStamp | null
  readonly error: string | null
}
