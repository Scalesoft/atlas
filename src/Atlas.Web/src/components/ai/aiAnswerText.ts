import type { AiSourceView } from "../../ai/assistantApi";
import { buildShellHashEncoded } from "../../shellRoute";
import t from "../../i18n/ai";

/** A piece of a sentence: plain text, inline code or a **bold** key fact. Nothing else is ever rendered from model text (plan R8, O5). */
export interface SentenceToken {
  kind: "text" | "code" | "strong";
  text: string;
}

/**
 * Splits a sentence at `code` and **bold** spans. A tag, a link or a URL stays text: the template prints tokens through
 * interpolation, so the browser never parses them. An unpaired backtick or asterisks stay text too.
 */
export function tokenizeSentence(text: string): SentenceToken[] {
  const tokens: SentenceToken[] = [];
  const span = /`([^`\r\n]+)`|\*\*([^*\r\n]+)\*\*/g;
  let from = 0;
  for (let match = span.exec(text); match; match = span.exec(text)) {
    if (match.index > from) tokens.push({ kind: "text", text: text.slice(from, match.index) });
    tokens.push(match[1] !== undefined ? { kind: "code", text: match[1] } : { kind: "strong", text: match[2]! });
    from = match.index + match[0].length;
  }
  if (from < text.length) tokens.push({ kind: "text", text: text.slice(from) });
  return tokens;
}

/** The current page at its heading, never a historical revision (AI-03). */
export function sourceRoute(source: Pick<AiSourceView, "pageId" | "headingId">): string {
  return buildShellHashEncoded({ view: "page", pageId: source.pageId, heading: source.headingId ?? undefined });
}

/** „Název › Nadpis“, the label of a source wherever it is named. */
export function sourceLabel(source: Pick<AiSourceView, "title" | "headingTitle">): string {
  return source.headingTitle ? `${source.title} › ${source.headingTitle}` : source.title;
}

/** „1 zdroj“, „2 zdroje“, „5 zdrojů“ (in English „1 source“, „2 sources“). */
export function sourceCount(n: number): string {
  return t.sources(n);
}
