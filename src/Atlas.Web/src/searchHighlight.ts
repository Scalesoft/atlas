export interface HighlightPart { text: string; matched: boolean }

/** Callout types of GitHub/Obsidian alerts (`> [!NOTE]`) the editor writes and imports bring. */
const CALLOUT_TYPES = "NOTE|TIP|IMPORTANT|WARNING|CAUTION|INFO|DANGER|SUCCESS|QUESTION|EXAMPLE|QUOTE|BUG|FAILURE|ABSTRACT|TODO|HINT|ERROR";
/** Bold labels that only name a callout („> **Poznámka**“, Wiki.js and older pages); the words after them stay. */
const CALLOUT_LABELS = "Poznámka|Poznámky|Tip|Tipy|Důležité|Upozornění|Varování|Pozor|Informace|Info|Note|Warning|Caution|Important";
/** A markdown escape (`\[`, `\*`, …) stands for the character itself; it is parked outside the syntax until the end. */
const ESCAPABLE = /\\([\\`*_{}[\]()#+\-.!|>~<=])/g;
const PARK = 0xE000;
const PARKED = new RegExp(`[${String.fromCharCode(PARK)}-${String.fromCharCode(PARK + 0xFF)}]`, "g");

/**
 * Search snippets and passages are cut from the page's markdown, so they carry raw markdown syntax; a reader never asked to
 * see `---`, `**bold**`, backticks, link brackets, `> [!NOTE]`, table pipes, Wiki.js `{.is-info}` or HTML tags in a two-line
 * preview. Strip the punctuation, keep the words (and the words of links, images and code). Applied before highlightSearch, so
 * match offsets are computed against the cleaned text and never point into removed syntax. Plain text only: the result is
 * rendered through text nodes, never as HTML.
 */
export function cleanSnippet(text: string): string {
  return text
    .replace(ESCAPABLE, (_, char: string) => String.fromCharCode(PARK + char.charCodeAt(0)))
    // A hard line break (`\` at the end of a line) is only a break.
    .replace(/\\(?=\s|$)/g, "")
    .replace(/<!--[\s\S]*?(?:-->|$)/g, " ")
    // Autolinks keep their address, even when the excerpt cut the closing bracket.
    .replace(/<((?:https?|ftp|mailto):[^<>\s]*)>?/gi, "$1")
    .replace(/<\/?[a-z][\w:-]*(?:\s[^<>]*)?\/?>/gi, " ")
    // Callouts: `> [!NOTE]` anywhere, a marker the excerpt cut at the start („WARNING] > …“), their bold title labels.
    .replace(new RegExp(`\\[!(?:${CALLOUT_TYPES})\\][+-]?`, "gi"), " ")
    .replace(new RegExp(`^(…\\s*)?!?(?:${CALLOUT_TYPES})\\][+-]?`), "$1")
    .replace(new RegExp(`(^|>\\s*|…\\s*)\\*\\*(?:${CALLOUT_LABELS})\\s*:?\\*\\*\\s*:?`, "giu"), "$1")
    .replace(/\{(?:\.[\w-]+|#[\w-]+)(?:\s+(?:\.[\w-]+|#[\w-]+))*\}/g, " ")
    // Code fences lose the fence and the language, inline code its backticks.
    .replace(/(?:```|~~~)[\w+#.-]*/g, " ")
    .replace(/!?\[([^\]]*)\]\([^)]*(?:\)|$)/g, "$1")
    .replace(/\[([^\]]+)\]\[[^\]]*\]/g, "$1")
    .replace(/\[\^[^\]]+\]/g, "")
    .replace(/(^|\s)[-*+]\s+\[[ xX]\](?=\s)/g, "$1")
    // Tables: the separator row goes, cells are separated by a middle dot.
    .replace(/\|?(?:\s*:?-{3,}:?\s*\|)+(?:\s*:?-{3,}:?\s*)?/g, " ")
    .replace(/(^|\s)\|(?=\s|$)/g, "$1·")
    .replace(/(^|\s)([-*_])\2{2,}(?=\s|$)/g, "$1 ")
    .replace(/`{1,3}([^`]*)`{1,3}/g, "$1")
    .replace(/\*\*([^*]+)\*\*/g, "$1")
    .replace(/__([^_]+)__/g, "$1")
    .replace(/~~([^~]+)~~/g, "$1")
    .replace(/==([^=]+)==/g, "$1")
    // Single-`*`/`_` emphasis, but only at a word boundary: without this, "snake_case_name"
    // loses its underscores and "a * b * c" (multiplication, not emphasis) loses its stars.
    // \p{L}\p{N} rather than \w: the content is Czech, and \w is ASCII-only.
    .replace(/(?<![\p{L}\p{N}_*])\*([^*\s][^*]*)\*(?![\p{L}\p{N}_*])/gu, "$1")
    .replace(/(?<![\p{L}\p{N}_])_([^_\s][^_]*)_(?![\p{L}\p{N}_])/gu, "$1")
    // Markers the excerpt left without their pair: `**` at a word edge, an unclosed `[`.
    .replace(/(?<=^|\s)\*{2,}(?=\S)|(?<=\S)\*{2,}(?=\s|$)/g, "")
    .replace(/\[(?=[^\]]*$)/g, "")
    .replace(/(^|\s)#{1,6}\s+/g, "$1")
    .replace(/(?<=^|\s)>\s?/g, "")
    .replace(/&(amp|lt|gt|quot|#39|nbsp);/g, entity => ({ '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&#39;': "'", '&nbsp;': ' ' })[entity] ?? entity)
    .replace(PARKED, (char) => String.fromCharCode(char.charCodeAt(0) - PARK))
    .replace(/\s+/g, " ")
    // Separators left at the ends or next to each other by removed cells.
    .replace(/(?:·\s*){2,}/g, "· ")
    .replace(/^[\s·]+|[\s·]+$/g, "")
    .trim();
}
const fold = (value: string) => value.normalize("NFD").replace(/\p{M}/gu, "").toLocaleLowerCase();
/** Return text segments; callers render text nodes, never query-generated HTML. */
export function highlightSearch(text: string, query: string): HighlightPart[] {
  const terms = [...new Set(fold(query).match(/[\p{L}\p{N}#+.]+/gu) ?? [])].filter(x => x.length > 1);
  if (!terms.length || !text) return [{ text, matched: false }];
  const characters = Array.from(text), folded: string[] = [], owners: number[] = [];
  characters.forEach((character, index) => { for (const unit of fold(character).split("")) { folded.push(unit); owners.push(index); } });
  const haystack = folded.join(""), selected = new Set<number>();
  for (const term of terms) {
    let start = haystack.indexOf(term);
    while (start >= 0) {
      for (let i = start; i < start + term.length; i++) if (owners[i] !== undefined) selected.add(owners[i]!);
      start = haystack.indexOf(term, start + term.length);
    }
  }
  const parts: HighlightPart[] = [];
  characters.forEach((character, index) => {
    const matched = selected.has(index), previous = parts.at(-1);
    if (previous?.matched === matched) previous.text += character;
    else parts.push({ text: character, matched });
  });
  return parts;
}
