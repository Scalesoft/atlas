import type { MarkdownDocumentSchema } from '../../core/types'

/** Exact host-supplied identity. Accounts and tenants never share recovery branches. */
export interface RecoveryScope {
  readonly serverId: string
  readonly tenantId: string
  readonly accountId: string
  readonly documentId: string
}

export interface RecoveryPendingUpdate {
  readonly id: string
  readonly update: Uint8Array
  readonly updateCount: number
  readonly attempted: boolean
}

/** A manual recovery copy, not a server-authorized replay log. */
export interface RecoveryCheckpoint {
  readonly formatVersion: 1
  readonly scope: RecoveryScope
  readonly branchId: string
  readonly revision: number
  readonly capturedAt: number
  readonly schema: MarkdownDocumentSchema | null
  readonly fragment: string
  readonly observedSequence: number
  readonly documentRevision: number
  readonly connectionGeneration: number
  readonly epoch: null
  readonly replayPolicy: 'manual-only'
  readonly yjsState: Uint8Array
  readonly pendingUpdates: readonly RecoveryPendingUpdate[]
}

export interface RecoverySummary {
  readonly scope: RecoveryScope
  readonly branchId: string
  readonly revision: number
  readonly capturedAt: number
  readonly pendingCount: number
  /** Binary content plus serialized metadata charged against the storage budget. */
  readonly byteLength: number
}

export interface RecoveryStore {
  write(checkpoint: RecoveryCheckpoint): Promise<void>
  read(scope: RecoveryScope, branchId: string): Promise<RecoveryCheckpoint | null>
  list(scope: RecoveryScope): Promise<RecoverySummary[]>
  discard(scope: RecoveryScope, branchId: string, expectedRevision: number): Promise<void>
  close(): void
}

export type RecoveryStoreErrorCode = 'invalid' | 'unavailable' | 'closed' | 'blocked' | 'conflict' | 'discarded' | 'quota' | 'corrupt' | 'storage'
export class RecoveryStoreError extends Error {
  constructor(readonly code: RecoveryStoreErrorCode, message: string, options?: ErrorOptions) {
    super(message, options)
    this.name = 'RecoveryStoreError'
  }
}
