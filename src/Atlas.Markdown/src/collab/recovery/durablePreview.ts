import { cloneJournal, journalSha256, type DurableJournalStore } from './durableJournal'
import { prepareMarkdownRecovery, preflight, RecoveryPreviewError, type MarkdownRecoveryPreviewOptions } from './preview'
import type { RecoveryCheckpoint, RecoveryStore } from './types'

export interface DurableRecoveryPreviewOptions extends Omit<MarkdownRecoveryPreviewOptions, 'store'> {
  readonly store: DurableJournalStore
  /** Exact epoch displayed with the selected branch; preview never grants replay permission. */
  readonly epoch: string
}

/** Export a selected V2 branch without connecting, claiming ownership or changing its queue. */
export function prepareDurableRecovery(options: DurableRecoveryPreviewOptions) {
  const epoch = options.epoch, store = options.store
  const signal = options.signal
  const maxBytes = options.maxCheckpointBytes ?? 16 * 1024 * 1024, maxNodes = options.maxNodes ?? 100_000, maxDepth = options.maxDepth ?? 100
  const active = () => { if (signal?.aborted) throw new RecoveryPreviewError('aborted', 'Recovery preview was cancelled.') }
  const reader: RecoveryStore = {
    read(scope, branch) {
      // Copy in the first fulfillment callback, before the host can reuse returned buffers.
      return store.read(scope, branch).then(async raw => {
        active()
        if (!raw) return null
        preflight(raw, maxBytes, maxNodes, maxDepth)
        let record
        try { record = cloneJournal(raw) }
        catch (cause) { throw new RecoveryPreviewError('content', 'The selected journal record is invalid.', { cause }) }
        if (record.epoch !== epoch) throw new RecoveryPreviewError('stale', 'The selected journal epoch has changed.')
        for (const item of record.pending) {
          active()
          if (await journalSha256(item.update) !== item.sha256) throw new RecoveryPreviewError('content', 'A journal update does not match its durable hash.')
        }
        active()
        return { formatVersion: 1, scope: record.scope, branchId: record.branchId, revision: record.revision,
          capturedAt: 0, schema: record.schema, fragment: record.fragment, observedSequence: record.sequence,
          documentRevision: record.revision, connectionGeneration: 0, epoch: null, replayPolicy: 'manual-only',
          yjsState: record.snapshot, pendingUpdates: record.pending.map(item => ({ id: item.updateId, update: item.update, updateCount: 1, attempted: true })) } satisfies RecoveryCheckpoint
      })
    },
    async write() { throw new Error('The journal preview cannot write.') },
    async list() { throw new Error('The journal preview cannot list unrelated branches.') },
    async discard() { throw new Error('The journal preview cannot discard.') }, close() {},
  }
  return prepareMarkdownRecovery({ ...options, store: reader })
}
