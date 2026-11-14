import * as Y from 'yjs'

interface Interval { start: number; end: number }
type ClientIntervals = Map<number, Interval[]>
interface DecodedCoverage { structs: ClientIntervals; deletes: ClientIntervals }

/** Coverage of known V1 updates. This is NOT a content/schema/snapshot-authority verifier. */
export interface UpdateCoverage {
  /** True if the candidate carries any struct clocks or deletion intervals absent from known updates. */
  hasMissing(update: Uint8Array): boolean
}

/**
 * Decode once when comparing several outbox entries against the same merged server state.
 * Compare encoded struct intervals directly: a state vector omits non-contiguous/pending structs.
 * Skip records mark missing ranges and must never count as known content. GC records do count.
 * Delete coverage is independent of struct clocks, including deletes whose target is still pending.
 *
 * Input must be Yjs update V1. Malformed/unsafe numeric ranges throw; they cannot acknowledge work.
 * Matching IDs are trusted to identify the same immutable payload within the existing CRDT session.
 * This deliberately does not compare payloads or authorize an untrusted snapshot's history.
 */
export function createUpdateCoverage(knownUpdate: Uint8Array): UpdateCoverage {
  const known = decodeCoverage(knownUpdate)
  return {
    hasMissing(update) {
      const candidate = decodeCoverage(update)
      return hasUncoveredIntervals(candidate.structs, known.structs)
        || hasUncoveredIntervals(candidate.deletes, known.deletes)
    },
  }
}

/** Candidate first, known merged update second. Use createUpdateCoverage to reuse decoded known state. */
export function hasMissingUpdateContent(update: Uint8Array, knownUpdate: Uint8Array): boolean {
  return createUpdateCoverage(knownUpdate).hasMissing(update)
}

function decodeCoverage(update: Uint8Array): DecodedCoverage {
  const decoded = Y.decodeUpdate(update)
  const structs: ClientIntervals = new Map(), deletes: ClientIntervals = new Map()
  for (const struct of decoded.structs) {
    const interval = checkedInterval(struct.id.client, struct.id.clock, struct.length)
    if (!(struct instanceof Y.Skip)) append(structs, struct.id.client, interval)
  }
  for (const [client, ranges] of decoded.ds.clients) {
    for (const range of ranges) append(deletes, client, checkedInterval(client, range.clock, range.len))
  }
  normalize(structs)
  normalize(deletes)
  return { structs, deletes }
}

function checkedInterval(client: number, start: number, length: number): Interval {
  const end = start + length
  if (!Number.isSafeInteger(client) || client < 0 || !Number.isSafeInteger(start) || start < 0
    || !Number.isSafeInteger(length) || length <= 0 || !Number.isSafeInteger(end) || end <= start) {
    throw new RangeError('Yjs update contains an invalid or unsafe clock interval.')
  }
  return { start, end }
}

function append(index: ClientIntervals, client: number, interval: Interval): void {
  const ranges = index.get(client)
  if (ranges) ranges.push(interval)
  else index.set(client, [interval])
}

function normalize(index: ClientIntervals): void {
  for (const [client, ranges] of index) {
    ranges.sort((left, right) => left.start - right.start)
    const merged: Interval[] = []
    for (const range of ranges) {
      const tail = merged.at(-1)
      if (tail && range.start <= tail.end) tail.end = Math.max(tail.end, range.end)
      else merged.push({ ...range })
    }
    index.set(client, merged)
  }
}

function hasUncoveredIntervals(candidate: ClientIntervals, known: ClientIntervals): boolean {
  for (const [client, ranges] of candidate) {
    const covered = known.get(client)
    if (!covered) return true
    let index = 0
    for (const range of ranges) {
      while (index < covered.length && covered[index].end <= range.start) index++
      const knownRange = covered[index]
      if (!knownRange || knownRange.start > range.start || knownRange.end < range.end) return true
    }
  }
  return false
}
