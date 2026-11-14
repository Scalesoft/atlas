import type { MarkdownEntityKind } from './types'

const name = (value: unknown): value is string => typeof value === 'string' && /^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$/.test(value)

/**
 * A kind of entity a plugin shows as an inline chip (a person, an issue, a page…), as data: describeMarkdownSchema writes it to the
 * manifest's `entities`, `recognizers` and `patterns`, which the server is to read instead of its own copy of the rules.
 * A pattern is the source of a regular expression that JavaScript
 * (flag `u`) and .NET read the same; the plugin that recognizes it compiles that source, never a copy. `\d`, `\w`, `\s`, `\b` and
 * their negations are refused: .NET reads them as any Unicode digit, letter or space, so spell the class out (`[0-9]`).
 */
export function defineMarkdownEntityKind(kind: MarkdownEntityKind): MarkdownEntityKind {
  const patterns = Object.entries(kind.patterns ?? {})
  if (!name(kind.kind) || typeof kind.inline !== 'string' || !kind.inline.trim()
    || patterns.some(([key, source]) => !name(key) || typeof source !== 'string' || !source)
    || (kind.recognizers ?? []).some(recognizer => !name(recognizer.id) || !Object.hasOwn(kind.patterns ?? {}, recognizer.pattern))) {
    throw new TypeError(`Invalid entity kind: ${String(kind.kind)}`)
  }
  for (const [key, source] of patterns) {
    try { new RegExp(source, 'u') } catch (error) { throw new TypeError(`Pattern ${key} of entity kind ${kind.kind} does not compile.`, { cause: error }) }
    if (/\\[dDwWsSbB]/u.test(source.replace(/\\\\/gu, ''))) {
      throw new TypeError(`Pattern ${key} of entity kind ${kind.kind} uses \\d, \\w, \\s or \\b, which .NET reads differently; spell the class out.`)
    }
  }
  return Object.freeze({ kind: kind.kind, inline: kind.inline,
    ...(kind.recognizers ? { recognizers: Object.freeze(kind.recognizers.map(recognizer => Object.freeze({ id: recognizer.id, pattern: recognizer.pattern }))) } : {}),
    ...(kind.patterns ? { patterns: Object.freeze({ ...kind.patterns }) } : {}) })
}
