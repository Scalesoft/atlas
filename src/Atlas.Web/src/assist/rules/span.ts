/**
 * A half-open range `[start, end)` of UTF-16 code units in the text a rule was given, the same unit as offsets
 * inside a ProseMirror text node. Rules only ever return spans that lie inside that text and are not empty.
 */
export interface TextSpan {
  readonly start: number;
  readonly end: number;
}

export function isSpanInside(text: string, span: TextSpan): boolean {
  return Number.isInteger(span.start) && Number.isInteger(span.end) && span.start >= 0 && span.start < span.end && span.end <= text.length;
}

/** Builds a span and verifies it against the text; a rule that computes an offset outside the text is a bug. */
export function spanOf(text: string, start: number, end: number): TextSpan {
  const span = { start, end };
  if (!isSpanInside(text, span)) throw new RangeError(`Span [${start}, ${end}) is outside a text of ${text.length} characters.`);
  return span;
}

export function spanText(text: string, span: TextSpan): string {
  if (!isSpanInside(text, span)) throw new RangeError(`Span [${span.start}, ${span.end}) is outside a text of ${text.length} characters.`);
  return text.slice(span.start, span.end);
}

export function spansOverlap(a: TextSpan, b: TextSpan): boolean {
  return a.start < b.end && b.start < a.end;
}

/**
 * Czech-aware word boundaries. `\b` in JavaScript knows only ASCII word characters, so it sees a boundary inside
 * "Upozornění" and none after it. Every rule regex uses these lookarounds with the `u` flag instead.
 */
export const wordStart = "(?<![\\p{L}\\p{N}])";
export const wordEnd = "(?![\\p{L}\\p{N}])";

/** A `u` regex for whole words: the alternatives must not continue or follow a letter or digit. */
export function words(alternatives: string, flags = "giu"): RegExp {
  return new RegExp(`${wordStart}(?:${alternatives})${wordEnd}`, flags);
}
