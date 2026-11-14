import type { MarkdownAssistApplyContext, MarkdownAssistBlock, MarkdownAssistSource, MarkdownAssistSuggestion, MarkdownAssistTrigger } from "@atlas/markdown/assist";
import { insertPageReference } from "@atlas/markdown/plugins/pages";
import type { Editor } from "@tiptap/core";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { api } from "../api";
import t from "../i18n/assist";
import { buildShellHash } from "../shellRoute";
import { fnv1a, linkPhrases, type LinkPhrase, type TextSpan } from "./rules";
import { spansOverlap } from "./rules/span";

/**
 * E2 „Propojení zmínek“ (plan §19, design §7.5 A): a finished paragraph's mention of a wiki page is offered as a link.
 * The phrases come from the browser (rules/linkPhrases.ts, never a paragraph with a password); POST
 * /api/assist/link-candidates answers exact title and alias matches, and pages close in meaning only with the capability
 * `editorLinks`. It is search, not model output: the marker carries the type icon `link`, never the AI glyph or tag.
 *
 * Two actions on one marker, each one undo step: „Ponechat text jako odkaz“ (default; a `link` mark to `#/pages/<id>`
 * over the author's words, the sentence stays) and „Nahradit názvem stránky“ (a page reference that follows renames).
 */
export interface LinkCandidate {
  /** The phrase as sent (the server echoes it exactly). */
  readonly phrase: string;
  readonly pageId: string;
  readonly spaceId: string;
  readonly spaceName: string;
  readonly title: string;
  readonly matchedBy: "title" | "alias" | "meaning";
}
export interface LinkCandidateRequest {
  readonly pageId: string;
  readonly phrases: readonly string[];
}
export type LinkCandidateFetch = (request: LinkCandidateRequest, signal: AbortSignal) => Promise<readonly LinkCandidate[]>;

/** The server. Every failure (404 without assistance, 400, 429, 503) rejects; the source then offers nothing, silently. */
export const fetchLinkCandidates: LinkCandidateFetch = async (request, signal) =>
  (await api<{ items?: LinkCandidate[] }>("/assist/link-candidates", { method: "POST", body: JSON.stringify(request), signal })).items ?? [];

const abortError = () => new DOMException("Aborted", "AbortError");
function delay(milliseconds: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) return reject(abortError());
    const timer = setTimeout(() => { signal.removeEventListener("abort", stop); resolve(); }, milliseconds);
    const stop = () => { clearTimeout(timer); reject(abortError()); };
    signal.addEventListener("abort", stop, { once: true });
  });
}

export interface LinkCandidateClient {
  request(pageId: string, phrases: readonly string[], signal: AbortSignal): Promise<LinkCandidate[]>;
}

/**
 * One per editor: at most one request in 3 s (the server's `ai-assist` limit is shared with E4), a newer paragraph
 * aborts the previous request, and a phrase already answered in this session is never asked again. A failed request
 * remembers nothing, so the next finished paragraph may ask again.
 */
export function createLinkCandidateClient(fetch: LinkCandidateFetch, options: { intervalMs?: number; remember?: number } = {}): LinkCandidateClient {
  const interval = options.intervalMs ?? 3_000;
  const limit = options.remember ?? 500;
  const known = new Map<string, readonly LinkCandidate[]>();
  const key = (pageId: string, phrase: string) => `${pageId}\u0000${phrase}`;
  let lastSent = Number.NEGATIVE_INFINITY;
  let current: AbortController | undefined;
  return {
    async request(pageId, phrases, signal) {
      const missing = [...new Set(phrases)].filter(phrase => !known.has(key(pageId, phrase)));
      if (missing.length) {
        current?.abort();
        const mine = (current = new AbortController());
        const forward = () => mine.abort();
        if (signal.aborted) mine.abort();
        signal.addEventListener("abort", forward, { once: true });
        try {
          const wait = lastSent + interval - Date.now();
          if (wait > 0) await delay(wait, mine.signal);
          if (mine.signal.aborted) throw abortError();
          lastSent = Date.now();
          const items = await fetch({ pageId, phrases: missing }, mine.signal);
          if (mine.signal.aborted) throw abortError();
          for (const phrase of missing) {
            known.set(key(pageId, phrase), items.filter(item => item.phrase === phrase));
            if (known.size > limit) known.delete(known.keys().next().value!);
          }
        } finally {
          signal.removeEventListener("abort", forward);
          if (current === mine) current = undefined;
        }
      }
      return phrases.flatMap(phrase => known.get(key(pageId, phrase)) ?? []);
    },
  };
}

const pageHref = /#\/pages\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?:[?/#]|$)/i;

/** Pages the document already links: page references and links holding `#/pages/<id>` (as the server's PageLink). */
export function linkedPageIds(doc: ProseMirrorNode): Set<string> {
  const ids = new Set<string>();
  doc.descendants(node => {
    if (node.type.name === "pageReference" && typeof node.attrs.id === "string") ids.add(node.attrs.id.toLowerCase());
    for (const mark of node.marks) {
      const id = mark.type.name === "link" ? pageHref.exec(String(mark.attrs.href ?? ""))?.[1] : undefined;
      if (id) ids.add(id.toLowerCase());
    }
    return true;
  });
  return ids;
}

/** Text offsets of a textblock that already link or are code: never offered again. */
export function markedSpans(block: ProseMirrorNode): TextSpan[] {
  const spans: TextSpan[] = [];
  block.forEach((child, offset) => {
    if (child.isText && child.marks.some(mark => mark.type.name === "link" || mark.type.name === "code"))
      spans.push({ start: offset, end: offset + child.nodeSize });
  });
  return spans;
}

interface Mention {
  readonly phrase: LinkPhrase;
  readonly pages: readonly LinkCandidate[];
}

/**
 * At most three mentions per paragraph that do not overlap: exact matches before meaning, a longer phrase before the
 * words inside it („Zálohování serverů“ before „Zálohování“); each page once; in reading order.
 */
export function chooseMentions(phrases: readonly LinkPhrase[], candidates: readonly LinkCandidate[], options: { exclude: ReadonlySet<string>; meaning: boolean; max?: number }): Mention[] {
  const byPhrase = new Map<string, LinkCandidate[]>();
  for (const candidate of candidates) {
    if (options.exclude.has(candidate.pageId.toLowerCase()) || (candidate.matchedBy === "meaning" && !options.meaning)) continue;
    byPhrase.set(candidate.phrase, [...byPhrase.get(candidate.phrase) ?? [], candidate]);
  }
  const exact = (phrase: LinkPhrase) => byPhrase.get(phrase.phrase)?.some(page => page.matchedBy !== "meaning") ? 0 : 1;
  const ranked = phrases.filter(phrase => byPhrase.has(phrase.phrase))
    .sort((a, b) => exact(a) - exact(b) || b.words - a.words || (b.span.end - b.span.start) - (a.span.end - a.span.start) || a.span.start - b.span.start);
  const offered = new Set<string>();
  const chosen: Mention[] = [];
  for (const phrase of ranked) {
    if (chosen.length >= (options.max ?? 3)) break;
    if (chosen.some(other => spansOverlap(other.phrase.span, phrase.span))) continue;
    const pages = byPhrase.get(phrase.phrase)!.filter(page => !offered.has(page.pageId.toLowerCase()));
    if (!pages.length) continue;
    for (const page of pages) offered.add(page.pageId.toLowerCase());
    chosen.push({ phrase, pages });
  }
  return chosen.sort((a, b) => a.phrase.span.start - b.phrase.span.start);
}

export interface LinkSourceOptions {
  readonly pageId: () => string | undefined;
  readonly client: LinkCandidateClient;
  readonly editor: () => Editor | undefined;
  /** Profil → AI „Propojení zmínek“ (`link-mention` not in `disabledSuggestionTypes`). */
  readonly enabled: () => boolean;
  /** Capability `editorLinks`: without it only exact matches (the server sends only those; this guards a stale answer). */
  readonly meaning: () => boolean;
  /** The adapter's finishing touch: the toast after Použít and the author's caret kept where they type. */
  readonly finish: (suggestion: MarkdownAssistSuggestion, message: string) => MarkdownAssistSuggestion;
}

export const linkMentionKind = "link.mention";
/** A finished paragraph: the caret left it, it was pasted, or the author asked (Alt+Enter); never the idle pause. */
const finished = new Set<MarkdownAssistTrigger>(["leave", "paste", "request"]);
const pageUrl = (id: string) => buildShellHash({ view: "page", pageId: id });

function reasonOf(page: LinkCandidate, phrase: string): string {
  if (page.matchedBy === "alias") return t.link.alias(phrase, page.title);
  if (page.matchedBy === "meaning") return t.link.meaning(phrase, page.title);
  return t.link.name(phrase);
}

/** Whether the whole range already links to `href` (Použít again is then no change). */
function linksTo(doc: ProseMirrorNode, range: { from: number; to: number }, href: string): boolean {
  let all = true;
  doc.nodesBetween(range.from, range.to, node => {
    if (node.isText && !node.marks.some(mark => mark.type.name === "link" && mark.attrs.href === href)) all = false;
    return all;
  });
  return all;
}

function suggestionsFor(block: MarkdownAssistBlock, mention: Mention, finish: LinkSourceOptions["finish"]): MarkdownAssistSuggestion[] {
  const { phrase, span } = mention.phrase;
  const range = block.rangeOf(span.start, span.end);
  const link = (page: LinkCandidate): MarkdownAssistSuggestion => {
    const href = pageUrl(page.pageId);
    return finish({
      id: `${linkMentionKind}:${fnv1a(phrase)}:${page.pageId}`,
      kind: linkMentionKind,
      title: t.link.title(phrase, page.title),
      description: t.link.description(page.spaceName),
      icon: "link",
      range,
      reason: reasonOf(page, phrase),
      apply(apply: MarkdownAssistApplyContext) {
        const target = apply.range;
        if (!target || linksTo(apply.editor.state.doc, target, href)) return false;
        // The link command acts on the selection; `replaceRange` would delete the words first. A selection is no history step.
        apply.editor.commands.setTextSelection(target);
        return apply.dispatch({ type: "set-link", href });
      },
    }, t.link.done);
  };
  const [top, ...others] = mention.pages;
  const reference = finish({
    id: `${linkMentionKind}.reference:${fnv1a(phrase)}:${top!.pageId}`,
    kind: linkMentionKind,
    title: t.link.replaceTitle,
    description: t.link.replaceDescription(top!.title),
    icon: "file-text",
    range,
    reason: reasonOf(top!, phrase),
    apply: (apply: MarkdownAssistApplyContext) => !!apply.range
      && apply.dispatch(insertPageReference({ id: top!.pageId, title: top!.title, url: pageUrl(top!.pageId) }), { replaceRange: apply.range }),
  }, t.link.replaceDone);
  return [link(top!), reference, ...others.map(link)];
}

export function createLinkSource(options: LinkSourceOptions): MarkdownAssistSource {
  return {
    id: "atlas-links",
    async suggest(block, { signal }) {
      const pageId = options.pageId();
      if (!pageId || !finished.has(block.trigger) || block.node.type.name !== "paragraph" || !options.enabled()) return [];
      const phrases = linkPhrases(block.text, { skip: markedSpans(block.node) });
      if (!phrases.length) return [];
      let candidates: LinkCandidate[];
      try {
        candidates = await options.client.request(pageId, phrases.map(phrase => phrase.phrase), signal);
      } catch {
        // Aborted, rate-limited, unavailable or assistance off (404): nothing to offer and nothing to say.
        return [];
      }
      const editor = options.editor();
      if (signal.aborted || !editor || editor.isDestroyed) return [];
      const exclude = linkedPageIds(editor.state.doc);
      exclude.add(pageId.toLowerCase());
      return chooseMentions(phrases, candidates, { exclude, meaning: options.meaning() })
        .flatMap(mention => suggestionsFor(block, mention, options.finish));
    },
  };
}
