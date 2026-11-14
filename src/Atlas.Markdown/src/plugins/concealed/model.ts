import type { MarkdownIconName } from '../../core'
import { concealedStrings } from './strings'

/** How readers uncover the block: by scratching the mist away, or only with an explicit Reveal button. */
export type ConcealedRevealMode = 'scratch' | 'button'
/** Per-block reader settings, stored in the document. Visual concealment only, never access control. */
export interface ConcealedContentSettings {
  readonly reveal: ConcealedRevealMode
  /** Delay before the mist returns once the reader leaves, 0–600000 ms, or null to stay uncovered. */
  readonly reconcealAfterMs: number | null
  /** Scratching only: percentage (10–100) scratched open after which the rest parts on its own, or null. */
  readonly completeAtPercent: number | null
}
export interface ConcealedContentLabels {
  readonly block: string; readonly reveal: string; readonly hide: string
  readonly scratch: string; readonly button: string; readonly revealMode: string
  readonly preview: string; readonly unwrap: string; readonly remove: string; readonly settings: string
}

export const NAME = 'concealedContent'
export const MODES: readonly ConcealedRevealMode[] = ['scratch', 'button']
export const MODE_ICONS: Record<ConcealedRevealMode, MarkdownIconName> = { scratch: 'highlighter', button: 'eye' }
export const DEFAULTS: ConcealedContentSettings = { reveal: 'scratch', reconcealAfterMs: 1500, completeAtPercent: 70 }
export type Choice = readonly [number | null, string, string]
export const seconds = (ms: number) => `${String(ms / 1000).replace('.', ',')} s`
/** Built when the panel opens, so a host's `setMarkdownStrings` call takes effect. */
export function reconcealChoices(): readonly Choice[] {
  const s = concealedStrings
  return [
    [0, s.reconcealNow, s.reconcealNowHint], [1500, s.reconcealAfter(seconds(1500)), s.reconcealShortHint],
    [5000, s.reconcealAfter(seconds(5000)), s.reconcealReadHint], [15000, s.reconcealAfter(seconds(15000)), s.reconcealLongHint],
    [60000, s.reconcealMinute, s.reconcealMinuteHint], [null, s.reconcealNever, s.reconcealNeverHint],
  ]
}
export function completeChoices(): readonly Choice[] {
  const s = concealedStrings
  return [[null, s.completeOff, s.completeOffHint], [50, s.completeHalf, s.completeHalfHint], [70, s.completeAt(70), s.completeRecommendedHint], [90, s.completeAt(90), s.completeMostHint]]
}
export function reconcealSummary(ms: number | null) {
  return ms === null ? concealedStrings.summaryNever : ms === 0 ? concealedStrings.summaryNow : concealedStrings.summaryAfter(seconds(ms))
}

export function validateMode(value: unknown): asserts value is ConcealedRevealMode {
  if (!MODES.includes(value as ConcealedRevealMode)) throw new TypeError('Invalid concealed reveal mode.')
}
export function validateReconceal(value: unknown): asserts value is number | null {
  if (value !== null && (typeof value !== 'number' || !Number.isInteger(value) || value < 0 || value > 600000)) throw new TypeError('Invalid concealed reconceal delay.')
}
export function validateComplete(value: unknown): asserts value is number | null {
  if (value !== null && (typeof value !== 'number' || !Number.isInteger(value) || value < 10 || value > 100)) throw new TypeError('Invalid concealed completion share.')
}
/** Validated settings from node attributes, with defaults for documents saved before they existed. */
export function settingsOf(attrs: Record<string, unknown>): ConcealedContentSettings {
  const reveal = attrs.reveal ?? DEFAULTS.reveal
  const reconcealAfterMs = attrs.reconcealAfterMs === undefined ? DEFAULTS.reconcealAfterMs : attrs.reconcealAfterMs
  const completeAtPercent = attrs.completeAtPercent === undefined ? DEFAULTS.completeAtPercent : attrs.completeAtPercent
  validateMode(reveal); validateReconceal(reconcealAfterMs); validateComplete(completeAtPercent)
  return { reveal, reconcealAfterMs, completeAtPercent }
}
