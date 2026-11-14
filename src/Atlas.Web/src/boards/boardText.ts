import type { BoardFontSize } from "./boardApi";

/** What the server takes as item text (prototype spec §5, server Rules.IsMultiLineText): at most 4000 characters. */
export const maxBoardText = 4000;
/** A connector label (interaction spec §4): one line, at most 200 characters. */
export const maxBoardLabel = 200;

// Every control character but the line feed (C0, DEL, C1), and the bidirectional controls ALM, LRM, RLM, LRE…RLO and LRI…PDI.
const refused = /[\u0000-\u0009\u000B-\u001F\u007F-\u009F\u061C\u200E\u200F\u202A-\u202E\u2066-\u2069]/g;

/**
 * Text as the server accepts it: a tab becomes four spaces, CR LF and a lone CR become a line break, every other control and
 * bidi character goes (the server refuses the whole batch for one), and the text is cut to its limit. `line` (a connector
 * label) also turns line breaks into spaces; `max` is another limit (a comment's 10 000).
 */
export function cleanBoardText(text: string, options: { line?: boolean; max?: number } = {}): string {
  let clean = text.replace(/\t/g, "    ").replace(/\r\n?/g, "\n").replace(refused, "");
  if (options.line) clean = clean.replace(/\n/g, " ");
  return clean.slice(0, options.max ?? (options.line ? maxBoardLabel : maxBoardText));
}

export const fontSizes: Record<BoardFontSize, number> = { s: 14, m: 18, l: 24, xl: 32 };
const AUTO_MAX = 32, AUTO_MIN = 12, PADDING = 12, LINE = 1.3, GLYPH = 0.55;

/**
 * The font size of a note (spec §3.2): a chosen size as it is; `auto` is the largest size from 32 down to 12 px at which the
 * text fits the note, and 12 px when it does not fit even so (the note does not grow, the text is clipped).
 * shortcut: estimated from an average glyph width, not measured; measure in the DOM if notes with wide scripts overflow.
 */
export function noteFontSize(text: string, width: number, height: number, size: "auto" | BoardFontSize = "auto"): number {
  if (size !== "auto") return fontSizes[size];
  // A link or chip shows its label, not its target (a contact chip's `a-contact:?…` would count as one word wider than any note).
  text = text.replace(/\]\([^)\s]*\)/g, "]");
  const paragraphs = text.split("\n").map(line => line.length);
  const longestWord = Math.max(0, ...text.split(/\s+/).map(word => word.length));
  for (let font = AUTO_MAX; font > AUTO_MIN; font--) {
    // A word wider than the note would break in the middle (BD2-4).
    if (longestWord * GLYPH * font > width - 2 * PADDING) continue;
    const perLine = Math.max(1, Math.floor((width - 2 * PADDING) / (GLYPH * font)));
    const lines = paragraphs.reduce((sum, length) => sum + Math.max(1, Math.ceil(length / perLine)), 0);
    if (lines * LINE * font + 2 * PADDING <= height) return font;
  }
  return AUTO_MIN;
}

interface Span { start: number; end: number; inserted: string }
/** The one span `text` changed in `base`: the common start and end kept, what lies between replaced. */
function changedSpan(base: string, text: string): Span {
  let start = 0;
  while (start < base.length && start < text.length && base[start] === text[start]) start++;
  let end = 0;
  while (end < base.length - start && end < text.length - start && base[base.length - 1 - end] === text[text.length - 1 - end]) end++;
  return { start, end: base.length - end, inserted: text.slice(start, text.length - end) };
}
const applied = (text: string, span: Span) => text.slice(0, span.start) + span.inserted + text.slice(span.end);

/**
 * A board text edited here (`ours`) while a colleague's (`theirs`) arrived, both from `base` (COL2-9): neither is overwritten. Edits
 * in separate places are both applied; edits of the same place (two people appending to it) keep both versions of it, the
 * colleague's first, and say so (`conflict`) for the person to tidy up.
 * shortcut: one changed span per side (the common start and end), so two edits far apart in one text count as one span reaching
 * between them; diff by words if notes edited in several places at once conflict too often.
 */
export function mergeBoardText(base: string, ours: string, theirs: string): { text: string; conflict: boolean } {
  if (theirs === base || theirs === ours) return { text: ours, conflict: false };
  if (ours === base) return { text: theirs, conflict: false };
  const mine = changedSpan(base, ours), other = changedSpan(base, theirs);
  // Two insertions at one place (both appended to the text) have no order of their own: both are kept, as one place.
  const oneInsertionPoint = mine.start === other.start && mine.start === mine.end && other.start === other.end;
  if (!oneInsertionPoint && (mine.end <= other.start || other.end <= mine.start)) {
    // The later span goes in first, so the earlier one keeps its offsets.
    const mineLater = mine.start !== other.start ? mine.start > other.start : mine.end !== other.end ? mine.end > other.end : true;
    const [later, earlier] = mineLater ? [mine, other] : [other, mine];
    return { text: applied(applied(base, later), earlier), conflict: false };
  }
  const start = Math.min(mine.start, other.start), end = Math.max(mine.end, other.end);
  const version = (text: string) => text.slice(start, text.length - (base.length - end));
  // A blank line between the versions, as two paragraphs; spaces at it would only be dropped by the editor.
  return { text: `${base.slice(0, start)}${version(theirs).trimEnd()}\n\n${version(ours).trimStart()}${base.slice(end)}`, conflict: true };
}
