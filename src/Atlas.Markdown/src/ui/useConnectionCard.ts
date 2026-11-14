/**
 * Decides when the corner card is allowed on screen.
 *
 * A collaborative session drops and recovers constantly — a laptop lid, a tunnel, a redeployed
 * server. Reacting to every one of those with a visible message is the flicker; the fix is the
 * same grace period a game uses before it admits the connection is in trouble. Escalations wait,
 * recoveries do not, and an unrecoverable failure never waits because there is nothing to
 * recover from.
 */
import { onScopeDispose, ref, watch, type Ref } from 'vue'
import type { ConnectionPresentation } from './connectionPresentation'

export interface ConnectionCardOptions {
  /** How long an interruption must last before the card is worth showing. */
  readonly graceMs?: number
}

const DEFAULT_GRACE_MS = 1200
const UNRECOVERABLE = 4

export function useConnectionCard(presentation: () => ConnectionPresentation, options: ConnectionCardOptions = {}): Ref<boolean> {
  const graceMs = options.graceMs ?? DEFAULT_GRACE_MS
  const visible = ref(false)
  let timer: ReturnType<typeof setTimeout> | undefined
  function clear(): void {
    if (timer !== undefined) { clearTimeout(timer); timer = undefined }
  }
  watch(presentation, current => {
    if (!current.card) { clear(); visible.value = false; return }
    if (current.severity >= UNRECOVERABLE) { clear(); visible.value = true; return }
    // One interruption turning into a worse one is still the same interruption: leave the
    // running timer alone so the person waits once, not once per transport state.
    if (visible.value || timer !== undefined) return
    timer = setTimeout(() => { timer = undefined; visible.value = true }, graceMs)
  }, { immediate: true })
  onScopeDispose(clear)
  return visible
}
