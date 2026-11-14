import { ref } from "vue";
import type { Editor } from "@tiptap/core";
import { foldText } from "../lib/foldText";
import { unfoldTo } from "./headingFold";
import { animateAtlas, literalMs } from "../motion/aiMotion";
import "./pageSearch.css";

// Hledat ve stránce (3.5 J): the side panel's search over the rendered page, in reading and in editing alike. The text of the
// page's DOM is read once into one string (and again only after the DOM changed); matches are Ranges painted through the
// CSS Custom Highlight API, so neither the DOM nor the document changes and no editor decoration is made per match.

/** What the reader typed, kept while the tab is away (Ctrl+F brings it back), and a request to focus the field. */
export const pageSearchQuery = ref("");
export const pageSearchFocus = ref(0);

// shortcut: at most this many matches are marked and listed (a one-letter search on a long page); raise with a virtual list.
export const MAX_MATCHES = 500;

/** The page's text and its folded form (case and diacritics away), each folded unit pointing back at its original characters. */
export interface FoldedText { original: string; folded: string; starts: Int32Array; ends: Int32Array }

/**
 * Folds text for search as foldText does everywhere in Atlas, with white space as one space (a block break, "\n", stays, so a
 * match never spans two blocks). ASCII is folded by hand: it is nearly all of the text and normalize() per character is slow.
 */
export function foldForSearch(original: string): FoldedText {
  let folded = "";
  const starts: number[] = [], ends: number[] = [];
  for (let i = 0; i < original.length;) {
    const code = original.codePointAt(i)!, size = code > 0xffff ? 2 : 1;
    const out = code === 10 ? "\n"
      : code < 128 ? (code >= 65 && code <= 90 ? String.fromCharCode(code + 32) : code === 9 || code === 13 || code === 12 || code === 11 ? " " : original[i]!)
      : /\s/u.test(String.fromCodePoint(code)) ? " " : foldText(original.slice(i, i + size));
    for (let unit = 0; unit < out.length; unit++) { starts.push(i); ends.push(i + size); }
    folded += out;
    i += size;
  }
  return { original, folded, starts: Int32Array.from(starts), ends: Int32Array.from(ends) };
}

export const searchNeedle = (query: string) => foldText(query).replace(/\s+/gu, " ").trim();

export interface TextMatch { start: number; end: number }

/** The matches of `query` in document order, as offsets into the original text; `more` when the limit cut them. */
export function findMatches(text: FoldedText, query: string, limit = MAX_MATCHES): { matches: TextMatch[]; more: boolean } {
  const needle = searchNeedle(query), matches: TextMatch[] = [];
  if (!needle) return { matches, more: false };
  for (let at = text.folded.indexOf(needle); at >= 0; at = text.folded.indexOf(needle, at + needle.length)) {
    if (matches.length === limit) return { matches, more: true };
    matches.push({ start: text.starts[at]!, end: text.ends[at + needle.length - 1]! });
  }
  return { matches, more: false };
}

/** The words around a match within its block, cut at word edges, with „…“ where the block goes on. */
export function snippetAround(original: string, match: TextMatch, before = 48, after = 72): string {
  const blockStart = original.lastIndexOf("\n", match.start - 1) + 1;
  const next = original.indexOf("\n", match.end), blockEnd = next < 0 ? original.length : next;
  let from = Math.max(blockStart, match.start - before), to = Math.min(blockEnd, match.end + after);
  if (from > blockStart) { const space = original.indexOf(" ", from); if (space >= 0 && space < match.start) from = space + 1; }
  if (to < blockEnd) { const space = original.lastIndexOf(" ", to); if (space > match.end) to = space; }
  const text = original.slice(from, to).replace(/\s+/gu, " ").trim();
  return `${from > blockStart ? "… " : ""}${text}${to < blockEnd ? " …" : ""}`;
}

/** The next or previous result, past the last back to the first and before the first to the last. */
export const stepResult = (index: number, count: number, delta: number) => count ? (((index + delta) % count) + count) % count : -1;

// --- The rendered page ------------------------------------------------------------------------------------------------

/** Controls and decorations inside the page's DOM that are not its text. */
const SKIP = "button, input, textarea, select, script, style, template, svg, [aria-hidden='true'], .atlas-heading-fold, .collaboration-carets__caret, [data-page-search-skip]";
const BLOCKS = new Set(["P", "LI", "H1", "H2", "H3", "H4", "H5", "H6", "TD", "TH", "TR", "PRE", "BLOCKQUOTE", "DIV", "SECTION", "ARTICLE", "FIGURE", "FIGCAPTION", "DT", "DD", "UL", "OL", "TABLE", "DETAILS", "SUMMARY", "HR"]);

export interface PageIndex { text: FoldedText; nodes: Text[]; nodeStarts: number[]; headings: (Element | null)[] }

const walk = (root: Element) => root.ownerDocument.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, {
  acceptNode: node => node.nodeType === 1 && (node as Element).matches(SKIP) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT,
});
/** An element's own words, without its controls (a heading's fold toggle). */
function textOf(element: Element) {
  let text = "";
  const walker = walk(element);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) if (node.nodeType === 3) text += (node as Text).data;
  return text.replace(/\s+/gu, " ").trim();
}

/**
 * The text of the page's rendered document (each top-level `.ProseMirror` under `root`, the static reading or the live editor),
 * hidden parts included: a folded section, a requirement's notes at a lower level and a block `content-visibility` skips are
 * still in the DOM. Each text node remembers the section heading it is under.
 */
export function indexPage(root: Element): PageIndex {
  const nodes: Text[] = [], nodeStarts: number[] = [], headings: (Element | null)[] = [];
  let original = "", heading: Element | null = null;
  const surfaces = [...root.querySelectorAll(".ProseMirror")].filter(surface => !surface.parentElement?.closest(".ProseMirror"));
  for (const surface of surfaces) {
    if (original && !original.endsWith("\n")) original += "\n";
    const walker = walk(surface);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      if (node.nodeType === 1) {
        const tag = (node as Element).tagName;
        if (tag === "BR") original += " ";
        else if (BLOCKS.has(tag) && original && !original.endsWith("\n")) original += "\n";
        if (/^H[1-6]$/.test(tag)) heading = node as Element;
        continue;
      }
      const data = (node as Text).data;
      if (!data) continue;
      nodes.push(node as Text); nodeStarts.push(original.length); headings.push(heading);
      original += data;
    }
  }
  return { text: foldForSearch(original), nodes, nodeStarts, headings };
}

/** The last text node that starts at or before `offset`. */
function nodeAt(index: PageIndex, offset: number) {
  let low = 0, high = index.nodeStarts.length - 1;
  while (low < high) { const middle = (low + high + 1) >> 1; if (index.nodeStarts[middle]! <= offset) low = middle; else high = middle - 1; }
  return low;
}

/** The match as a DOM Range, or null when the DOM changed under the index. */
export function rangeOf(index: PageIndex, match: TextMatch): Range | null {
  const first = nodeAt(index, match.start), last = nodeAt(index, match.end - 1);
  const start = index.nodes[first], end = index.nodes[last];
  if (!start?.isConnected || !end?.isConnected) return null;
  try {
    const range = start.ownerDocument.createRange();
    range.setStart(start, match.start - index.nodeStarts[first]!);
    range.setEnd(end, match.end - index.nodeStarts[last]!);
    return range;
  } catch { return null; }
}

/** Where a match lies: its section heading and the requirement block it is in. */
export function placeOf(index: PageIndex, match: TextMatch): string {
  const at = nodeAt(index, match.start);
  const requirement = index.nodes[at]?.parentElement?.closest<HTMLElement>("[data-requirement-view]")?.dataset.requirementTitle;
  const heading = index.headings[at];
  return [heading && textOf(heading), requirement].filter(Boolean).join(" › ");
}

/** The live editor that owns a node, when the page is being edited (tiptap keeps it on its view's root). */
export function editingEditor(node: Node): Editor | undefined {
  const surface = node.parentElement?.closest(".ProseMirror");
  return surface?.getAttribute("contenteditable") === "true" ? (surface as Element & { editor?: Editor }).editor : undefined;
}

/** Blocks the reading view showed in full for a match; they go back as they were when the search moves on. */
let shown: HTMLElement[] = [];
export function hideShownBlocks() { shown.forEach(block => block.classList.remove("is-current", "has-current")); shown = []; }

/**
 * Brings a match out of what hides it: a folded section opens (unfoldTo), a folded requirement block opens (both the reader's
 * own folds, as the outline and the caret open them), and a requirement whose level hides the text shows in full. While editing
 * the match becomes the editor's selection, which does the last (requirementViewMode's `is-current`); in reading the block gets
 * the same class until the search moves on. The DOM's text is never touched.
 */
export function revealMatch(range: Range) {
  const node = range.startContainer, element = node.parentElement;
  if (!element) return;
  hideShownBlocks();
  // A lazy part of the editor it is in builds its full look before it is seen (@atlas/markdown proximity.ts).
  element.dispatchEvent(new Event("md-reveal", { bubbles: true }));
  unfoldTo(element);
  const editor = editingEditor(node);
  if (editor && !editor.isDestroyed) {
    try {
      const from = editor.view.posAtDOM(range.startContainer, range.startOffset), to = editor.view.posAtDOM(range.endContainer, range.endOffset);
      editor.commands.setTextSelection({ from, to });
    } catch { /* the editor rebuilt this part: the highlight still shows where it is */ }
    return;
  }
  if (element.getClientRects().length) return;
  const blocks: HTMLElement[] = [];
  for (let block = element.closest<HTMLElement>("[data-requirement-view]"); block; block = block.parentElement?.closest<HTMLElement>("[data-requirement-view]") ?? null) blocks.push(block);
  blocks.forEach((block, at) => block.classList.add(at ? "has-current" : "is-current"));
  shown = blocks;
}

/** A ring over the match that closes on it and fades once the page has moved to it (transform and opacity only; a fade under reduced motion). */
export function pulseMatch(range: Range) {
  const rect = range.getBoundingClientRect();
  if (!rect.width && !rect.height) return;
  const ring = document.createElement("div");
  ring.className = "page-search-pulse";
  ring.setAttribute("aria-hidden", "true");
  Object.assign(ring.style, { left: `${rect.left - 4}px`, top: `${rect.top - 3}px`, width: `${rect.width + 8}px`, height: `${rect.height + 6}px` });
  document.body.append(ring);
  const animation = animateAtlas(ring, [
    { opacity: 0, transform: "scale(1.35)" }, { opacity: 1, transform: "scale(1)", offset: 0.4 }, { opacity: 0, transform: "scale(1)" },
  ], { duration: literalMs(ring, 700), fill: "forwards" });
  if (animation) void animation.finished.finally(() => ring.remove());
  else ring.remove();
}
