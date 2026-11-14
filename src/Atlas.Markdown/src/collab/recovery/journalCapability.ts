import type { MarkdownCollaborationAdapter } from '../../core/types'

export interface MarkdownJournalCapability {
  /** Await local commits already captured when called; this does not wait for server ACKs. */
  flushLocal(): Promise<void>
}
const capabilities = new WeakMap<MarkdownCollaborationAdapter, MarkdownJournalCapability>()
export function registerJournalCapability(adapter: MarkdownCollaborationAdapter, capability: MarkdownJournalCapability): () => void {
  const registered = Object.freeze({ flushLocal: () => capability.flushLocal() })
  capabilities.set(adapter, registered)
  return () => { if (capabilities.get(adapter) === registered) capabilities.delete(adapter) }
}
/** Available only while a V2 adapter session is bound to a controller. */
export function getMarkdownJournalCapability(adapter: MarkdownCollaborationAdapter): MarkdownJournalCapability | null {
  return capabilities.get(adapter) ?? null
}
