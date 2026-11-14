import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";
import t from "../i18n/contacts";
import { api } from "../api";
import { contactProps } from "../boards/boardTargets";
import type { ObjectApi, ObjectSummary } from "../objects/objectApi";
import { contactTypeId } from "./contactData";
import { contactHitsOf, type ContactSearch, type SearchItemLike } from "./contactHits";
import { objectApi } from "../objects/objectApi";
import { usageApi, type UsageApi } from "../objects/relationsApi";
import type { Lexicon } from "../linking/lexicon";
import { mergePeople, rankPeople, serverTerms, teachesAlias, type PersonHit } from "./contactSearch";

/**
 * How much a directory contact is used in the page's space and in all (the contacts plugin's `usage`, which ranks the people of one
 * name): the usage service's count and its first page of places, both cached by the service. Null when the contact is not readable.
 */
export const contactUsage = (spaceId: () => string | undefined, usage: Pick<UsageApi, "count" | "usage"> = usageApi, related: () => ReadonlySet<string> = () => new Set()) =>
  async (objectId: string, { signal }: { signal: AbortSignal }): Promise<{ here: number; total: number; related?: boolean } | null> => {
    const [count, places] = await Promise.all([usage.count(objectId, signal), usage.usage(objectId, null, signal)]);
    if (!count) return null;
    const space = spaceId();
    return { here: space ? (places?.items ?? []).filter(row => row.spaceId === space).length : 0, total: count.total,
      ...(related().has(objectId.toLowerCase()) && { related: true }) };
  };

/** An active Atlas account as the page editor's people endpoint answers it (`GET /api/pages/{id}/people`). */
export interface PagePerson { id: string; name: string; username?: string | null; email?: string | null }

/** An account as an editor's contact: the snapshot a contact stores, refreshable by its id (`directoryId`). */
export function accountContact(person: PagePerson): MarkdownContact {
  return {
    name: person.name,
    // The login people also look each other up by (MarkdownContact.username, editor 0.8.0+).
    ...(person.username ? { username: person.username } : {}),
    ...(person.email ? { emails: [person.email] } : {}),
    directoryId: person.id,
    kind: "internal",
    internalUser: { id: person.id, name: person.name },
  };
}

/** The wiki search (`GET /api/search`), whose pages list their contact widgets. */
export const searchWiki: ContactSearch = async (text, signal) => (await api<{ items?: SearchItemLike[] }>(`/search?q=${encodeURIComponent(text)}&limit=20`, { signal })).items ?? [];

/** The accounts of the page editor's people endpoint (an editor of the page only). */
export const pageAccounts = (pageId: string) => (text: string, signal: AbortSignal) =>
  api<PagePerson[]>(`/pages/${encodeURIComponent(pageId)}/people?q=${encodeURIComponent(text)}`, { signal });

export interface PeopleSources {
  /** The wiki contact directory (`atlas.contact`). */
  directory?: Pick<ObjectApi, "list">;
  /** Contacts written in pages. */
  wiki?: ContactSearch;
  /** Atlas accounts. */
  accounts?: (text: string, signal: AbortSignal) => Promise<PagePerson[]>;
}

/**
 * Everyone the sources know who matches `text` (contactSearch.ts): the accounts, then the directory, then the contacts written only in
 * pages, each person once, the best match first. A source that matches one piece of text is asked for the longest word (and what a
 * nickname stands for); the wiki search gets the whole text. A failed source fails the search.
 */
export async function findPeople(text: string, signal: AbortSignal, sources: PeopleSources): Promise<PersonHit[]> {
  const terms = serverTerms(text);
  if (!terms.length) return [];
  const each = <T>(ask: (term: string) => Promise<T[]>) => Promise.all(terms.map(ask)).then(lists => lists.flat());
  const [listed, accounts, pages] = await Promise.all([
    sources.directory ? each(term => sources.directory!.list({ type: contactTypeId, scope: "wiki", q: term }, signal).then(list => list.items)) : [],
    sources.accounts ? each(term => sources.accounts!(term, signal)) : [],
    sources.wiki ? sources.wiki(text, signal) : [],
  ]);
  const hits = peopleHits(accounts, listed);
  // A page's linked contact carries its directory object id: mergePeople then keeps it as that directory contact, never a second row.
  for (const hit of contactHitsOf(pages)) hits.push({ key: `page:${hit.key}`, contact: hit.contact, source: "page", origin: hit.source ? t.sources.pageOf(hit.source) : t.sources.page,
    ...(hit.contact.objectId && { objectId: hit.contact.objectId }) });
  return rankPeople(text, mergePeople(hits));
}

function peopleHits(accounts: readonly PagePerson[], listed: readonly ObjectSummary[]): PersonHit[] {
  // An account first: the same person in the directory then is that account, whose contact the editor can refresh.
  const hits: PersonHit[] = accounts.map(person => ({ key: `user:${person.id}`, contact: accountContact(person), source: "account", origin: t.sources.account }));
  for (const item of listed) {
    // The title is the name's plain text; the data's name may be Markdown.
    const contact = contactProps({ ...(item.data ?? {}), name: item.title });
    // The aliases stay on the hit, never in the contact: a page's snapshot does not carry them.
    const { aliases, aliasPicks } = (item.data ?? {}) as { aliases?: unknown; aliasPicks?: unknown };
    if (contact) hits.push({ key: `obj:${item.id}`, contact, source: "directory", objectId: item.id, origin: t.sources.directory,
      ...(Array.isArray(aliases) && { aliases: aliases.filter((alias): alias is string => typeof alias === "string") }),
      ...(typeof aliasPicks === "object" && aliasPicks !== null && { aliasPicks: aliasPicks as Record<string, number> }) });
  }
  return hits;
}

/** A pick after searching for `query` teaches the directory contact the query as its alias (contactSearch.ts `teachesAlias`); who may not
 * change the contact teaches nothing (the server refuses, the pick stands). */
export function learnAlias(hit: Pick<PersonHit, "contact" | "objectId">, query: string, objects: Pick<ObjectApi, "change"> = objectApi): void {
  if (teachesAlias(query, hit)) void objects.change(hit.objectId!, { learnAlias: query.trim() }).catch(() => undefined);
}

/** The editor's `learn` of a contact directory (`@MMu`, an underlined alias picked): learnAlias for the snapshot picked, by its object id. */
export const learnPicked = (query: string, contact: MarkdownContact, objects: Pick<ObjectApi, "change"> = objectApi) =>
  learnAlias({ contact, ...(contact.objectId && { objectId: contact.objectId }) }, query, objects);

/** A roster person as the contacts plugin's `known` takes it: the snapshot, its object id and the directory's aliases (names.ts reads them). */
export const knownContact = (hit: PersonHit): MarkdownContact & { aliases?: readonly string[] } =>
  ({ ...hit.contact, ...(hit.objectId && { objectId: hit.objectId }), ...(hit.aliases?.length && { aliases: hit.aliases }) });

/**
 * The space's lexicon people (linking/lexicon.ts) as hits of the people search, in its order (the most used in the space first): a
 * directory contact by its object id with its aliases (and its account's id), an account as `accountContact` without the login and e-mail
 * the lexicon leaves out.
 */
export function lexiconHits(lexicon: Pick<Lexicon, "people">): PersonHit[] {
  return lexicon.people.map(person => {
    const contact: MarkdownContact = { name: person.name, ...(person.org && { organization: person.org }), ...(person.role && { title: person.role }),
      ...(person.userId && { directoryId: person.userId }) };
    return person.contactId
      ? { key: `obj:${person.contactId}`, contact, source: "directory", objectId: person.contactId, origin: t.sources.directory, ...(person.aliases.length && { aliases: person.aliases }) }
      : { key: `user:${person.userId}`, contact: { ...contact, kind: "internal", internalUser: { id: person.userId!, name: person.name } }, source: "account", origin: t.sources.account };
  });
}
