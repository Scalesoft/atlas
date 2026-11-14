import type { ContactDirectoryService, MarkdownContact } from "@atlas/markdown/plugins/contacts";
import type { MarkdownPageReference, MarkdownPageSource } from "@atlas/markdown/plugins/pages";
import type { YouTrackIssue } from "@atlas/markdown/plugins/youtrack";
import { foldText } from "../lib/foldText";

/**
 * Resolvers of the rule hits (plan §12 H.2, H.3): a rule only proposes a candidate; the authoritative source confirms it
 * with the reader's authorization before anything is offered (principle P3: nothing found, no suggestion). One set per
 * live editor, so caches never outlive the editing session.
 */

const folded = (value: string) => foldText(value).trim();

function abortError(): DOMException {
  return new DOMException("Aborted", "AbortError");
}

/**
 * YouTrack keys without a projects endpoint (decision O4): `resolve` with a negative cache, at most one request at a
 * time. A key the server reports missing (404, also an invisible issue) is never asked again in this session and
 * the rules stop proposing it (`isKnownMissing` feeds `detectIntents`). A failed request caches nothing.
 */
export function createIssueResolver(resolve: (key: string, context: { readonly signal: AbortSignal }) => Promise<YouTrackIssue | null>) {
  const missing = new Set<string>();
  const found = new Map<string, YouTrackIssue>();
  let queue: Promise<unknown> = Promise.resolve();
  return {
    isKnownMissing: (key: string) => missing.has(key),
    async resolve(key: string, signal: AbortSignal): Promise<YouTrackIssue | null> {
      if (missing.has(key)) return null;
      const known = found.get(key);
      if (known) return known;
      const turn = queue.then(async () => {
        if (signal.aborted) throw abortError();
        if (missing.has(key)) return null;
        const cached = found.get(key);
        if (cached) return cached;
        const issue = await resolve(key, { signal });
        if (issue) found.set(key, issue);
        else missing.add(key);
        return issue;
      });
      queue = turn.catch(() => undefined);
      return turn;
    },
  };
}

const caseEnding = /(?:ovi|ové|ová|ovou|em|ou|ům|ech|a|e|i|u|y|ě)$/u;
/**
 * A crude Czech stem for the search (the server matches a lower-case substring with diacritics): „Nováka“ → „novák“,
 * „Jana“ → „jan“. Never shorter than three letters.
 */
function stem(word: string): string {
  const base = word.toLowerCase();
  const cut = base.replace(caseEnding, "");
  return cut.length >= 3 ? cut : base.slice(0, 3);
}
const nameWords = (contact: MarkdownContact) => folded(contact.internalUser?.name ?? contact.name).split(/[\s-]+/).filter(Boolean);
const emailOf = (contact: MarkdownContact) => contact.emails?.map(folded) ?? [];

export type ContactMatch =
  | { readonly kind: "one"; readonly contact: MarkdownContact }
  | { readonly kind: "many" }
  | { readonly kind: "none" };

/**
 * Only an unambiguous person becomes a suggestion with the contact; several candidates become „Vložit kontakt — vybrat
 * z adresáře…“, none becomes nothing. The text may be inflected („kontaktujte Jana Nováka“): when the name as written
 * finds nobody, the surname's stem is searched and every written word must start a word of the name.
 */
export async function matchContact(directory: ContactDirectoryService, query: string, signal: AbortSignal): Promise<ContactMatch> {
  const term = folded(query);
  if (!term) return { kind: "none" };
  const email = term.includes("@");
  const unique = (candidates: readonly MarkdownContact[]): ContactMatch =>
    candidates.length === 1 ? { kind: "one", contact: candidates[0]! } : candidates.length ? { kind: "many" } : { kind: "none" };
  const direct = await directory.search(query.trim(), { signal });
  if (email) {
    return unique(direct.filter(contact => emailOf(contact).includes(term)));
  }
  if (direct.length > 1) {
    // „Jan Novák“ and „Jan Nováková“: the exact full name decides.
    const exact = direct.filter(contact => nameWords(contact).join(" ") === term.replace(/[\s-]+/g, " "));
    return exact.length === 1 ? unique(exact) : { kind: "many" };
  }
  if (direct.length) return unique(direct);
  const words = query.trim().split(/\s+/).filter(Boolean);
  if (words.length < 2) return { kind: "none" };
  const stems = words.map(stem);
  return unique((await directory.search(stems.at(-1)!, { signal }))
    .filter(contact => stems.every(part => nameWords(contact).some(word => word.startsWith(folded(part))))));
}

/** A pasted page URL (E6): the page the reader may open, through the editor's batched reference check. */
export async function resolvePage(pages: MarkdownPageSource, id: string, signal: AbortSignal): Promise<MarkdownPageReference | null> {
  return pages.resolve ? pages.resolve(id, { signal }) : null;
}
