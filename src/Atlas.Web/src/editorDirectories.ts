import type { ContactDirectoryService, MarkdownContact } from "@atlas/markdown/plugins/contacts";
import t from "./i18n/shell";
import type { MarkdownPageReference, MarkdownPageSource } from "@atlas/markdown/plugins/pages";
import { api, ApiError } from "./api";
import { accountContact, contactUsage, findPeople, knownContact, learnPicked, lexiconHits, pageAccounts, searchWiki, type PagePerson } from "./contacts/contactSources";
import { mergePeople, rankPeople, type PersonHit } from "./contacts/contactSearch";
import { foldText } from "./lib/foldText";
import { relatedPeople } from "./assist/relatedPeople";
import { lexiconOf, lexiconReady } from "./linking/lexicon";
import { objectApi } from "./objects/objectApi";
import { referenceAnswers, referencesOnTheWay } from "./linking/pageReferenceAnswers";
import { buildShellHashEncoded } from "./shellRoute";
import { verificationMark } from "./verification/verificationMark";

/** `type` "board": a board of the Boards module (POST /api/pages/references answers it among the pages, UX PG4-4). */
interface Titled { readonly id: string; readonly title: string; readonly type?: string; readonly verification?: string }
interface Found extends Titled { readonly spaceId?: string; readonly spaceName?: string; readonly isDraft?: boolean; readonly matchedAlias?: string | null }

/** Ověřený obsah (5.2): the link chip draws the verification's shield before the title (display only, as every `icon`). */
const markOf = (state: string | undefined) => { const mark = verificationMark(state); return mark ? { icon: mark.icon, ...(mark.warn ? { warn: true } : {}) } : {}; };
const pageUrl = (id: string) => buildShellHashEncoded({ view: "page", pageId: id });
const reference = (page: Titled): MarkdownPageReference => page.type === "board"
  ? { id: page.id, title: page.title, url: buildShellHashEncoded({ view: "board", boardId: page.id }), icon: "layout-dashboard" }
  : { id: page.id, title: page.title, url: pageUrl(page.id), ...markOf(page.verification) };
/**
 * F2: a search result in the `[[` list shows its space and whether it is a draft („Provoz · Koncept“). `detail` is
 * display-only (editor 0.9.0): the inserted node and a resolved link never carry it.
 */
function listed(page: Found): MarkdownPageReference {
  const detail = [page.spaceName, page.isDraft ? t.common.draftShort : null].filter(Boolean).join(" · ");
  return { ...reference(page), ...(detail ? { detail } : {}) };
}

async function orNull<T>(request: Promise<T>): Promise<T | null> {
  try { return await request; }
  catch (error) {
    if (error instanceof ApiError && error.status === 404) return null;
    throw error;
  }
}

const Limit = 10;

interface AtlasPageSource extends MarkdownPageSource {
  matchUrl(url: string): boolean;
  resolveUrl(url: string, context: { signal: AbortSignal }): Promise<MarkdownPageReference | null>;
}

/** Only a whole-page link of this Atlas instance; section anchors keep their ordinary-link semantics. */
export function pageIdFromUrl(value: string, kind: "pages" | "boards" = "pages"): string | null {
  try {
    const url = new URL(value);
    if (!["https:", "http:"].includes(url.protocol) || url.username || url.password
      || url.origin !== location.origin || url.pathname !== location.pathname || url.search) return null;
    const match = new RegExp(`^#/${kind}/([^/?#]+)$`).exec(url.hash);
    if (!match) return null;
    const id = decodeURIComponent(match[1]!);
    return id && !/[\u0000-\u001f\u007f/\\]/u.test(id) ? id : null;
  } catch { return null; }
}

const startsAWord = (text: string, term: string) => ` ${foldText(text).replace(/[^\p{L}\p{N}]+/gu, " ")}`.includes(` ${term}`);

/**
 * Exact title, title prefix, a word of the title, a hit through an alias, a word of the space's name („Zač“ finds the pages of
 * Začínáme), then the rest (the server's body and fuzzy hits) in the order of the server.
 */
function matchRank(page: Found, term: string) {
  const title = foldText(page.title);
  if (title === term) return 0;
  if (title.startsWith(term)) return 1;
  if (startsAWord(page.title, term)) return 2;
  if (page.matchedAlias) return 3;
  return page.spaceName && startsAWord(page.spaceName, term) ? 4 : 5;
}

/** E4: what the `[[` list may be ordered by — the page being edited and the section around the caret (≤ 2 000 characters). */
export interface PageOrderSection {
  readonly pageId: string;
  readonly context: string;
}
const OrderBudgetMs = 300;
const OrderCandidates = 20;
interface Ranked { readonly page: Found; readonly group: number; readonly rank: number }

/**
 * POST /api/assist/page-order within 300 ms: the similarity of each readable candidate to the section, or `null` when the
 * answer is late, empty (the server's own budget ran out), unavailable (503) or failed. A late answer is aborted and dropped.
 */
async function sectionSimilarity(section: PageOrderSection, candidates: readonly string[], signal: AbortSignal): Promise<Map<string, number> | null> {
  const controller = new AbortController();
  const stop = () => controller.abort();
  signal.addEventListener("abort", stop, { once: true });
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    const answer = api<{ items?: { pageId: string; similarity: number }[] }>("/assist/page-order", {
      method: "POST", body: JSON.stringify({ pageId: section.pageId, context: section.context, candidates }), signal: controller.signal,
    }).catch(() => null);
    const late = new Promise<null>(resolve => { timer = setTimeout(() => resolve(null), OrderBudgetMs); });
    const result = await Promise.race([answer, late]);
    return result?.items?.length ? new Map(result.items.map(item => [item.pageId.toLowerCase(), item.similarity])) : null;
  } finally {
    clearTimeout(timer);
    controller.abort();
    signal.removeEventListener("abort", stop);
  }
}

/**
 * Similarity orders only inside each group (current space, other spaces), and an exact title stays first: the pages with a
 * similarity swap places among themselves, most similar first; a page without one (a draft, a page the server left out)
 * keeps its lexical place.
 */
function bySimilarity(ordered: readonly Ranked[], similarity: ReadonlyMap<string, number>): Ranked[] {
  const result = [...ordered];
  const score = (item: Ranked) => similarity.get(item.page.id.toLowerCase())!;
  for (const group of new Set(result.map(item => item.group))) {
    const slots = result.flatMap((item, index) => item.group === group && item.rank !== 0 && similarity.has(item.page.id.toLowerCase()) ? [index] : []);
    const moved = slots.map(index => result[index]!).sort((a, b) => score(b) - score(a));
    slots.forEach((slot, index) => { result[slot] = moved[index]!; });
  }
  return result;
}

/**
 * Page links of the editor (`[[` and the slash menu). The wiki search (published pages and the drafts the reader may see, never
 * people) runs twice at once: over every space and over the current one, so the current space is never crowded out by the limit.
 * The current space comes first, then the others; in each group an exact title, a title prefix and an alias hit precede the rest,
 * which keeps the order of the server (the space-scoped answer first). A link resolves from the loaded space tree when it can and
 * otherwise from a batched reference check; the server still authorizes the visit.
 *
 * E4 (plan §19): with `order` answering a section (capability `editorLinks` and an attached assist adapter), the lexical
 * order is refined by the section's similarity for at most 300 ms; typing never waits for more, and an answer for an
 * older query is dropped.
 */
export function createPageSource(pages: () => readonly Titled[], options: { spaceId?: () => string | undefined; pageId?: () => string | undefined; order?: () => PageOrderSection | null } = {}): AtlasPageSource {
  const verify = createReferenceVerifier();
  let generation = 0;
  return {
    currentId: options.pageId,
    matchUrl: url => pageIdFromUrl(url) !== null || pageIdFromUrl(url, "boards") !== null,
    async resolveUrl(url, { signal }) {
      // A board's title comes from the batched check (one cheap query), never from the whole board (GET /api/boards/{id}).
      const board = pageIdFromUrl(url, "boards");
      if (board) {
        const found = await verify(board, signal);
        return found?.type === "board" ? reference(found) : null;
      }
      const id = pageIdFromUrl(url);
      if (!id) return null;
      const page = await orNull(api<Titled>(`/pages/${encodeURIComponent(id)}`, { signal }));
      return page && page.id === id ? reference(page) : null;
    },
    async search(query, { signal }) {
      const mine = ++generation;
      const spaceId = options.spaceId?.();
      // No query yet: the first item is what Enter picks, so it is the pages the reader visited last, this space's first (not a random page).
      if (!query.trim()) {
        const { items } = await api<{ items: Found[] }>(`/me/library/recent?offset=0&limit=${Limit}`, { signal });
        return [...items].sort((a, b) => Number(!!spaceId && a.spaceId !== spaceId) - Number(!!spaceId && b.spaceId !== spaceId)).map(listed);
      }
      const path = (scope = "") => `/search?q=${encodeURIComponent(query)}${scope}&limit=${Limit}`;
      const [everywhere, here] = await Promise.all([
        api<{ items: Found[] }>(path(), { signal }),
        spaceId ? api<{ items: Found[] }>(path(`&spaceId=${encodeURIComponent(spaceId)}`), { signal }) : undefined,
      ]);
      const term = foldText(query.trim());
      const unique = [...new Map([...(here?.items ?? []), ...everywhere.items].map(page => [page.id, page])).values()];
      const group = (page: Found) => spaceId && page.spaceId !== spaceId ? 1 : 0;
      let ordered: Ranked[] = unique
        .map((page, index) => ({ page, index, group: group(page), rank: matchRank(page, term) }))
        .sort((a, b) => a.group - b.group || a.rank - b.rank || a.index - b.index)
        .slice(0, Limit);
      const section = ordered.length > 1 ? options.order?.() : null;
      if (section) {
        const similarity = await sectionSimilarity(section, ordered.slice(0, OrderCandidates).map(({ page }) => page.id), signal);
        if (signal.aborted) throw new DOMException("Aborted", "AbortError");
        if (similarity && mine === generation) ordered = bySimilarity(ordered, similarity);
      }
      return ordered.map(({ page }) => listed(page));
    },
    async resolve(id, { signal }) {
      const known = pages().find(page => page.id === id);
      if (known) return reference(known);
      const page = await verify(id, signal);
      return page && reference(page);
    },
  };
}

const ReferenceBatch = 100;
const pageId = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
interface Waiting { id: string; signal: AbortSignal; settle: (page: Titled | null) => void; fail: (error: unknown) => void }

export { forgetPageReferences } from "./linking/pageReferenceAnswers";
const referenceTtlMs = 30_000;
/**
 * Verifies rendered links in batches: every resolve of one task joins one POST /api/pages/references (at most 100 ids each).
 * The server answers only the pages (and boards, `type`) the reader may open, with the title it would see, reads no bodies and records no opening;
 * a missing id is unavailable. An id that is not a page id never reaches the server. A failed request fails its links.
 */
const kept = (id: string, page: Titled): Titled => ({ id, title: page.title, ...(page.type ? { type: page.type } : {}), ...(page.verification ? { verification: page.verification } : {}) });
function createReferenceVerifier() {
  let waiting: Waiting[] = [];
  const titled = (entry: Waiting, titles: Map<string, Titled>) => {
    const page = titles.get(entry.id.toLowerCase());
    entry.settle(entry.signal.aborted || page === undefined ? null : kept(entry.id, page));
  };
  async function verifyBatch(batch: Waiting[]) {
    // Another editor of the page (the static page and the live editor) may have sent the same ids a moment ago, in the same task.
    const joined = batch.filter(entry => !entry.signal.aborted && referencesOnTheWay.has(entry.id.toLowerCase()));
    for (const entry of joined) referencesOnTheWay.get(entry.id.toLowerCase())!.then(titles => titled(entry, titles), entry.fail);
    const live = batch.filter(entry => !entry.signal.aborted && !joined.includes(entry));
    const ids = [...new Set(live.map(entry => entry.id.toLowerCase()))];
    for (let start = 0; start < ids.length; start += ReferenceBatch) {
      const chunk = ids.slice(start, start + ReferenceBatch);
      const asking = live.filter(entry => chunk.includes(entry.id.toLowerCase()));
      const request = api<{ items: Titled[] }>("/pages/references", { method: "POST", body: JSON.stringify({ ids: chunk }) })
        .then(found => new Map(found.items.map(page => [page.id.toLowerCase(), page])));
      for (const id of chunk) referencesOnTheWay.set(id, request);
      void request.catch(() => {}).finally(() => { for (const id of chunk) if (referencesOnTheWay.get(id) === request) referencesOnTheWay.delete(id); });
      try {
        const titles = await request;
        const at = Date.now();
        for (const id of chunk) referenceAnswers.set(id, { at, page: titles.get(id) ?? null });
        for (const entry of asking) {
          const page = titles.get(entry.id.toLowerCase());
          entry.settle(page === undefined ? null : kept(entry.id, page));
        }
      } catch (error) {
        for (const entry of asking) entry.fail(error);
      }
    }
    for (const entry of batch) if (entry.signal.aborted) entry.settle(null);
  }
  return (id: string, signal: AbortSignal) => new Promise<Titled | null>((settle, fail) => {
    if (!pageId.test(id)) return settle(null);
    const known = referenceAnswers.get(id.toLowerCase());
    if (known && Date.now() - known.at < referenceTtlMs)
      return settle(known.page && kept(id, known.page));
    if (waiting.length === 0) queueMicrotask(() => {
      const batch = waiting;
      waiting = [];
      void verifyBatch(batch);
    });
    waiting.push({ id, signal, settle, fail });
  });
}

/** The space's lexicon people (linking/lexicon.ts) as people-search hits, the most used in the space first; none before its first answer. */
function knownPeopleOf(spaceId: string | undefined): PersonHit[] {
  const lexicon = lexiconOf(spaceId);
  return lexicon ? lexiconHits(lexicon) : [];
}

/**
 * Contacts and `@` mentions: the shared people search (contacts/contactSources.ts) over the Atlas accounts (GET /api/pages/{id}/people,
 * editors of the page only; a board or a page not saved yet has none), the wiki contact directory and the contacts written in pages,
 * merged with the space's lexicon, each person once. The lexicon also gives initials and learned aliases, which the servers' word search
 * does not, and is what a picker shows in its first render (`cached`, P17): the search never waits for it. A contact stores a snapshot;
 * refreshing it (an account only) is an explicit action of the plugin.
 */
export const createPeopleSearch = (pageId: () => string | undefined, spaceId: () => string | undefined = () => undefined, related = relatedPeople) => {
  const search = async (text: string, signal: AbortSignal) => {
    const id = pageId();
    const found = await findPeople(text, signal, { ...(id ? { accounts: pageAccounts(id) } : {}), directory: objectApi, wiki: searchWiki });
    const known = knownPeopleOf(spaceId());
    return known.length ? rankPeople(text, mergePeople([...found, ...known]), related(id)) : found;
  };
  const cached = (text: string) => rankPeople(text, knownPeopleOf(spaceId()), related(pageId()));
  return Object.assign(search, { cached });
};

/**
 * The people the editor recognises by full name and learned alias (contacts plugin `known`, contacts/contactSources.ts `knownContact`):
 * the space's lexicon, so every editor of the space, a reopened page and the people search ask nothing more.
 */
export const createKnownPeople = (spaceId: () => string | undefined) => async (): Promise<MarkdownContact[]> => {
  const lexicon = await lexiconReady(spaceId());
  return lexicon ? lexiconHits(lexicon).map(knownContact) : [];
};

export function createContactDirectory(pageId: () => string | undefined, spaceId: () => string | undefined = () => undefined): ContactDirectoryService {
  const search = createPeopleSearch(pageId, spaceId);
  const people = (id: string) => `/pages/${encodeURIComponent(id)}/people`;
  // A directory contact goes in by its object id (as ContactPicker's pick), so `@` links it without a question.
  const contactOf = (hit: PersonHit) => (hit.objectId ? { ...hit.contact, objectId: hit.objectId } : hit.contact);
  return {
    async search(query, { signal }) {
      return !pageId() && !spaceId() ? [] : (await search(query, signal)).map(contactOf);
    },
    // The lexicon's people for the text: the `@` list's first rows before the search answers (P17).
    cached: query => search.cached(query).map(contactOf),
    async get(userId, { signal }) {
      const id = pageId();
      // Without a page (a board) the account as the space's lexicon knows it.
      if (!id) return knownPeopleOf(spaceId()).find(hit => hit.source === "account" && hit.contact.directoryId === userId)?.contact ?? null;
      const person = await orNull(api<PagePerson>(`${people(id)}/${encodeURIComponent(userId)}`, { signal }));
      return person && accountContact(person);
    },
    // `@MMu`, an underlined alias or a written name picked: the directory contact may learn the text (contactSearch.ts `teachesAlias`).
    learn: (query, contact) => learnPicked(query, contact),
  };
}

/** Every people service of a document editor in one place (page, its comments, a board's texts), for the page `pageId` of the space. */
export function createPeopleServices(pageId: () => string | undefined, spaceId: () => string | undefined) {
  return { contacts: createContactDirectory(pageId, spaceId), people: createPeopleSearch(pageId, spaceId), knownPeople: createKnownPeople(spaceId),
    contactUsage: contactUsage(spaceId, undefined, () => relatedPeople(pageId())) };
}
export type PeopleServices = ReturnType<typeof createPeopleServices>;
