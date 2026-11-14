import * as Y from 'yjs'
import { afterEach, describe, expect, it } from 'vitest'
import { createUpdateCoverage, hasMissingUpdateContent } from './updateCoverage'

const documents: Y.Doc[] = []
const empty = Uint8Array.of(0, 0)
function doc(gc = true) {
  const value = new Y.Doc({ gc })
  documents.push(value)
  return value
}
const encode = (value: Y.Doc) => Y.encodeStateAsUpdate(value)
function captureChange(value: Y.Doc, change: () => void): Uint8Array {
  const vector = Y.encodeStateVector(value)
  change()
  return Y.encodeStateAsUpdate(value, vector)
}
afterEach(() => { for (const value of documents.splice(0)) value.destroy() })

describe('V1 update coverage', () => {
  it('recognizes the empty update without manufacturing missing work', () => {
    expect(hasMissingUpdateContent(empty, empty)).toBe(false)
    const value = doc(); value.getText('content').insert(0, 'known')
    expect(hasMissingUpdateContent(empty, encode(value))).toBe(false)
    expect(hasMissingUpdateContent(encode(value), empty)).toBe(true)
  })

  it('does not mistake historical delete sets for a readonly client back-sync', () => {
    const server = doc(); server.getText('content').insert(0, 'historical content')
    server.getText('content').delete(0, 11)
    const known = encode(server), local = doc()
    Y.applyUpdate(local, known)
    const residual = Y.encodeStateAsUpdate(local, Y.encodeStateVectorFromUpdate(known))
    expect(residual.length).toBeGreaterThan(2)
    expect(Y.decodeUpdate(residual).structs).toHaveLength(0)
    expect(hasMissingUpdateContent(residual, known)).toBe(false)
  })

  it('retains a genuinely new deletion even when every struct clock is already known', () => {
    const local = doc(); local.getText('content').insert(0, 'abc')
    const known = encode(local)
    const deletion = captureChange(local, () => local.getText('content').delete(1, 1))
    expect(hasMissingUpdateContent(deletion, known)).toBe(true)
    expect(hasMissingUpdateContent(deletion, Y.mergeUpdates([known, deletion]))).toBe(false)
  })

  it('distinguishes partial from full deletion coverage using real Yjs edits', () => {
    const local = doc(); local.getText('content').insert(0, 'abcd')
    const baseline = encode(local)
    const first = captureChange(local, () => local.getText('content').delete(0, 1))
    const second = captureChange(local, () => local.getText('content').delete(0, 1))
    const partial = Y.mergeUpdates([baseline, first])
    expect(hasMissingUpdateContent(second, partial)).toBe(true)
    expect(hasMissingUpdateContent(second, Y.mergeUpdates([partial, second]))).toBe(false)
  })

  it('covers only known portions of a longer content update', () => {
    const local = doc(); local.getText('content').insert(0, 'ab')
    const known = encode(local)
    local.getText('content').insert(2, 'cd')
    expect(hasMissingUpdateContent(encode(local), known)).toBe(true)
    expect(hasMissingUpdateContent(known, encode(local))).toBe(false)
  })

  it('recognizes a pending struct already retained in a non-contiguous merged update', () => {
    const source = doc(); source.getText('content').insert(0, 'a')
    const baseline = encode(source)
    const pending = captureChange(source, () => source.getText('content').insert(1, 'b'))
    const incomplete = doc(); Y.applyUpdate(incomplete, pending)
    expect(incomplete.store.pendingStructs).not.toBeNull()
    expect(hasMissingUpdateContent(pending, encode(incomplete))).toBe(false)
    expect(hasMissingUpdateContent(baseline, encode(incomplete))).toBe(true)
    expect(hasMissingUpdateContent(encode(source), Y.mergeUpdates([pending, baseline]))).toBe(false)
  })

  it('does not count Skip holes between retained pending updates as coverage', () => {
    const source = doc()
    const first = captureChange(source, () => source.getText('content').insert(0, 'a'))
    const middle = captureChange(source, () => source.getText('content').insert(1, 'b'))
    const last = captureChange(source, () => source.getText('content').insert(2, 'c'))
    const sparse = Y.mergeUpdates([last, first])
    expect(Y.decodeUpdate(sparse).structs.some(struct => struct instanceof Y.Skip)).toBe(true)
    expect(hasMissingUpdateContent(last, sparse)).toBe(false)
    expect(hasMissingUpdateContent(middle, sparse)).toBe(true)
    expect(hasMissingUpdateContent(sparse, Y.mergeUpdates([first, middle, last]))).toBe(false)
  })

  it('recognizes pending delete sets without requiring their target structs to be integrated', () => {
    const source = doc(); source.getText('content').insert(0, 'a')
    const baseline = encode(source)
    const deletion = captureChange(source, () => source.getText('content').delete(0, 1))
    const incomplete = doc(); Y.applyUpdate(incomplete, deletion)
    expect(incomplete.store.pendingDs).not.toBeNull()
    expect(hasMissingUpdateContent(deletion, encode(incomplete))).toBe(false)
    expect(hasMissingUpdateContent(baseline, encode(incomplete))).toBe(true)
  })

  it('handles independently authored updates, reordered delivery and duplicates', () => {
    const a = doc(), b = doc()
    a.getText('a').insert(0, 'alpha'); b.getText('b').insert(0, 'beta')
    const aInsert = encode(a), bInsert = encode(b)
    const aDelete = captureChange(a, () => a.getText('a').delete(0, 1))
    const bDelete = captureChange(b, () => b.getText('b').delete(1, 1))
    const known = Y.mergeUpdates([bDelete, aInsert, bInsert, aDelete, aInsert, bDelete])
    const coverage = createUpdateCoverage(known)
    for (const candidate of [aInsert, bInsert, aDelete, bDelete, known]) expect(coverage.hasMissing(candidate)).toBe(false)
    expect(hasMissingUpdateContent(bDelete, Y.mergeUpdates([aInsert, bInsert, aDelete]))).toBe(true)
  })

  it('recognizes GC and retained-content encodings of the same known clocks and delete sets', () => {
    const retained = doc(false); retained.getText('content').insert(0, 'deleted content')
    retained.getText('content').delete(0, 8)
    const collected = doc(); Y.applyUpdate(collected, encode(retained))
    expect([...encode(retained)]).not.toEqual([...encode(collected)])
    expect(hasMissingUpdateContent(encode(retained), encode(collected))).toBe(false)
    expect(hasMissingUpdateContent(encode(collected), encode(retained))).toBe(false)
  })

  it('does not confuse GC struct clocks with absent content', () => {
    const known = gcRange(7, 0, 10)
    expect(Y.decodeUpdate(known).structs[0]).toBeInstanceOf(Y.GC)
    expect(hasMissingUpdateContent(gcRange(7, 3, 4), known)).toBe(false)
    expect(hasMissingUpdateContent(gcRange(7, 8, 4), known)).toBe(true)
  })

  it.each([
    { name: 'identical', candidate: [[2, 3]], known: [[2, 3]], missing: false },
    { name: 'inside larger interval', candidate: [[3, 2]], known: [[0, 8]], missing: false },
    { name: 'adjacent fragments cover', candidate: [[0, 6]], known: [[0, 2], [2, 2], [4, 2]], missing: false },
    { name: 'overlapping fragments cover', candidate: [[0, 6]], known: [[0, 4], [2, 4]], missing: false },
    { name: 'unsorted duplicate fragments cover', candidate: [[0, 6]], known: [[4, 2], [0, 4], [0, 4]], missing: false },
    { name: 'one-clock interior gap', candidate: [[0, 6]], known: [[0, 2], [3, 3]], missing: true },
    { name: 'missing first clock', candidate: [[0, 6]], known: [[1, 5]], missing: true },
    { name: 'missing last clock', candidate: [[0, 6]], known: [[0, 5]], missing: true },
    { name: 'disjoint candidate intervals', candidate: [[0, 2], [4, 2]], known: [[0, 6]], missing: false },
    { name: 'empty known delete set', candidate: [[0, 1]], known: [], missing: true },
  ])('delete interval coverage: $name', ({ candidate, known, missing }) => {
    expect(hasMissingUpdateContent(deleteRanges(23, candidate), deleteRanges(23, known))).toBe(missing)
  })

  it('never covers another client deletion with the same clock interval', () => {
    expect(hasMissingUpdateContent(deleteRanges(24, [[0, 2]]), deleteRanges(23, [[0, 2]]))).toBe(true)
  })

  it.each([2 ** 31, 2 ** 32, Number.MAX_SAFE_INTEGER - 16])('uses safe numeric arithmetic at large clocks %s', start => {
    expect(hasMissingUpdateContent(deleteRanges(31, [[start + 1, 6]]), deleteRanges(31, [[start, 8]]))).toBe(false)
    expect(hasMissingUpdateContent(deleteRanges(31, [[start, 8]]), deleteRanges(31, [[start + 1, 7]]))).toBe(true)
    expect(hasMissingUpdateContent(gcRange(31, start + 1, 6), gcRange(31, start, 8))).toBe(false)
    expect(hasMissingUpdateContent(gcRange(31, start, 8), gcRange(31, start, 7))).toBe(true)
  })

  it('throws rather than acknowledging malformed data or an unsafe exclusive clock end', () => {
    expect(() => hasMissingUpdateContent(Uint8Array.of(255), empty)).toThrow()
    expect(() => hasMissingUpdateContent(empty, Uint8Array.of(255))).toThrow()
    expect(() => hasMissingUpdateContent(deleteRanges(31, [[Number.MAX_SAFE_INTEGER, 1]]), empty)).toThrow(RangeError)
    expect(() => hasMissingUpdateContent(empty, deleteRanges(31, [[Number.MAX_SAFE_INTEGER, 1]]))).toThrow(RangeError)
  })

  it('owns its decoded coverage even if the caller later mutates the input bytes', () => {
    const source = doc(); source.getText('content').insert(0, 'stable')
    const known = encode(source), candidate = known.slice(), coverage = createUpdateCoverage(known)
    known.fill(255)
    expect(coverage.hasMissing(candidate)).toBe(false)
  })

  it('agrees with an independent clock-position set oracle in 1000 deterministic multi-client cases', () => {
    type Span = { kind: 'struct' | 'delete'; client: number; start: number; length: number }
    let seed = 0x51a7c0de
    const random = (limit: number) => {
      seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0
      return seed % limit
    }
    // Deliberately expand bounded clocks into individual identities. The oracle has
    // no sorting, merging, binary search or interval-containment algorithm.
    const positions = (spans: Span[]) => {
      const set = new Set<string>()
      for (const span of spans) for (let clock = span.start; clock < span.start + span.length; clock++) {
        set.add(`${span.kind}:${span.client}:${clock}`)
      }
      return set
    }
    const wire = (spans: Span[]) => spans.length ? Y.mergeUpdates(spans.map(span => span.kind === 'struct'
      ? gcRange(span.client, span.start, span.length)
      : deleteRanges(span.client, [[span.start, span.length]]))) : empty
    let missingCases = 0, coveredCases = 0, executed = 0
    for (let index = 0; index < 1000; index++) {
      const candidate: Span[] = [], known: Span[] = []
      for (const client of [11, 23, 97]) for (const kind of ['struct', 'delete'] as const) {
        const length = 3 + random(6)
        // Regularly hit both clock zero and the exclusive upper boundary 80.
        const start = index % 7 === 0 ? 0 : index % 7 === 1 ? 80 - length : random(81 - length)
        const span = { kind, client, start, length }
        candidate.push(span)
        switch (index % 5) {
          case 0: { // Adjacent fragments cover one larger candidate exactly.
            const split = 1 + random(length - 1)
            known.push({ ...span, length: split }, { ...span, start: start + split, length: length - split })
            break
          }
          case 1: { // A one-clock interior hole must survive merged GC Skip records.
            const gap = 1 + random(length - 2)
            known.push({ ...span, length: gap }, { ...span, start: start + gap + 1, length: length - gap - 1 })
            break
          }
          case 2: // Partial prefix/suffix coverage misses a candidate boundary.
            known.push(random(2) ? { ...span, length: length - 1 } : { ...span, start: start + 1, length: length - 1 })
            break
          case 3: // Duplicated overlapping larger ranges fully cover the candidate.
            known.push(span, span, { ...span, start: Math.max(0, start - 1), length: length + (start > 0 ? 1 : 0) })
            candidate.push({ ...span, start: start + 1, length: length - 2 })
            break
          default: { // Arbitrary overlap, missing clients and unrelated known clocks.
            const otherStart = random(80), otherLength = 1 + random(80 - otherStart)
            known.push({ kind, client: random(3) ? client : 131, start: otherStart, length: otherLength })
          }
        }
      }
      // Both formats are merged in a varying order, including repeated input chunks.
      if (index % 2) known.reverse()
      if (index % 3) candidate.reverse()
      const candidatePositions = positions(candidate), knownPositions = positions(known)
      const expected = [...candidatePositions].some(position => !knownPositions.has(position))
      const knownWire = wire(known), candidateWire = wire(candidate)
      expect(hasMissingUpdateContent(candidateWire, knownWire), `case ${index}: ${JSON.stringify({ candidate, known })}`).toBe(expected)
      expect(createUpdateCoverage(knownWire).hasMissing(candidateWire), `reused coverage case ${index}`).toBe(expected)
      if (expected) missingCases++; else coveredCases++
      executed++
    }
    expect(executed).toBe(1000)
    expect(missingCases).toBeGreaterThanOrEqual(400)
    expect(coveredCases).toBeGreaterThanOrEqual(400)
  })

  it('documents that clock/delete coverage does not authenticate conflicting same-ID payloads', () => {
    const first = doc(), conflicting = doc(); conflicting.clientID = first.clientID
    first.getText('content').insert(0, 'cat'); conflicting.getText('content').insert(0, 'dog')
    expect(hasMissingUpdateContent(encode(conflicting), encode(first))).toBe(false)
    expect(first.getText('content').toString()).not.toBe(conflicting.getText('content').toString())
  })
})

// Public Yjs V1 encoder methods; writeLen is a varuint write, also used for framing counts.
// Deliberately construct interval layouts without allocating huge documents or clock-sized arrays.
function deleteRanges(client: number, ranges: readonly number[][]): Uint8Array {
  const encoder = new Y.UpdateEncoderV1()
  encoder.writeLen(0) // no struct client blocks
  encoder.writeLen(ranges.length ? 1 : 0)
  if (ranges.length) {
    encoder.writeClient(client)
    encoder.writeLen(ranges.length)
    for (const [clock, length] of ranges) { encoder.writeDsClock(clock); encoder.writeDsLen(length) }
  }
  return encoder.toUint8Array()
}

function gcRange(client: number, clock: number, length: number): Uint8Array {
  const encoder = new Y.UpdateEncoderV1()
  encoder.writeLen(1) // one struct client block
  encoder.writeLen(1) // one struct
  encoder.writeClient(client)
  encoder.writeLen(clock)
  new Y.GC(Y.createID(client, clock), length).write(encoder, 0)
  encoder.writeLen(0) // no explicit delete-set client blocks
  return encoder.toUint8Array()
}
