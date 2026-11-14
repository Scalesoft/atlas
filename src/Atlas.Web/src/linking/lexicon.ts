import { computed, shallowRef, toValue, watch, type MaybeRefOrGetter } from "vue";
import { api, ApiError } from "../api";
import { readStored, writeStored } from "../lib/browserStorage";
import type { MarkdownLexiconSource } from "@atlas/markdown/core";

/** A person of a space's lexicon (server `LexiconPerson`): `key` is the one identity key (`object:<contactId>`, else `user:<id>`). */
export interface LexiconPerson {
  key: string;
  contactId: string | null;
  userId: string | null;
  name: string;
  /** Learned aliases („MMu“, „Mára“); initials are derived from the name, never stored. */
  aliases: readonly string[];
  org: string | null;
  role: string | null;
  /** Readable published pages of the space that name the person. */
  uses: number;
}

/**
 * What the linking engine recognises while writing in a space (GET /api/spaces/{id}/lexicon, audit engine D5): the shape of the
 * editor core's `MarkdownLexicon`. `truncated`: a list hit the server's cap, so nothing may be chipped on its own.
 */
export interface Lexicon {
  version: string;
  truncated: boolean;
  people: readonly LexiconPerson[];
  pages: readonly { id: string; title: string }[];
  youTrackProjects: readonly string[];
}

/** How long an answer counts as current before the next use revalidates it (by ETag, mostly a 304). */
const FreshMs = 60_000;
const memory = new Map<string, Lexicon>();
const checked = new Map<string, number>();
const asking = new Map<string, Promise<Lexicon | undefined>>();
/** Spaces whose revalidation waits for an idle moment; a request that runs meanwhile takes the space off. */
const scheduled = new Set<string>();
/** Bumped by every new answer, so `useLexicon` re-reads; the maps themselves stay plain (read inside computeds). */
const revision = shallowRef(0);
let account: string | null = null;

const storageKey = (spaceId: string) => `atlas:lexicon:${account}:${spaceId}`;
const whenIdle = (run: () => void) => (globalThis.requestIdleCallback ? globalThis.requestIdleCallback(() => run()) : setTimeout(run, 1));

/**
 * The signed-in account (App.vue): the stored copies are per account, and a change of account forgets every lexicon in memory.
 * Without an account nothing is stored, only kept for the visit.
 */
export function setLexiconAccount(id: string | null): void {
  if (id === account) return;
  account = id;
  memory.clear(); checked.clear(); asking.clear(); scheduled.clear();
  revision.value++;
}

function stored(spaceId: string): Lexicon | undefined {
  if (!account) return undefined;
  try {
    const lexicon = JSON.parse(readStored(storageKey(spaceId)) ?? "null") as Lexicon | null;
    return lexicon && typeof lexicon.version === "string" && Array.isArray(lexicon.people) && Array.isArray(lexicon.pages) ? lexicon : undefined;
  } catch { return undefined; }
}

function keep(spaceId: string, lexicon: Lexicon | undefined) {
  if (lexicon) memory.set(spaceId, lexicon); else memory.delete(spaceId);
  if (account) {
    if (lexicon) writeStored(storageKey(spaceId), JSON.stringify(lexicon));
    else try { localStorage.removeItem(storageKey(spaceId)); } catch { /* nothing stored */ }
  }
  revision.value++;
}

/**
 * The last known lexicon of the space at once (memory, else this account's stored copy), or undefined before the first answer.
 * A stale one is revalidated in idle time; nothing here waits for the network.
 */
export function lexiconOf(spaceId: string | undefined): Lexicon | undefined {
  if (!spaceId) return undefined;
  let lexicon = memory.get(spaceId);
  if (!lexicon && (lexicon = stored(spaceId))) memory.set(spaceId, lexicon);
  if (Date.now() - (checked.get(spaceId) ?? 0) > FreshMs && !scheduled.has(spaceId)) {
    scheduled.add(spaceId);
    whenIdle(() => { if (scheduled.delete(spaceId)) void refreshLexicon(spaceId); });
  }
  return lexicon;
}

/** Asks the server now, with the known version as If-None-Match; one request per space at a time. Resolves to the current lexicon
 * (the known one on a 304 or a failure, none once the space is out of reach). */
export function refreshLexicon(spaceId: string): Promise<Lexicon | undefined> {
  const running = asking.get(spaceId);
  if (running) return running;
  scheduled.delete(spaceId);
  const who = account, known = memory.get(spaceId) ?? stored(spaceId);
  checked.set(spaceId, Date.now());
  const request = api<Lexicon>(`/spaces/${encodeURIComponent(spaceId)}/lexicon`, known ? { headers: { "If-None-Match": `"${known.version}"` } } : {})
    .then(lexicon => { if (who === account) keep(spaceId, lexicon); return lexicon; }, (failure: unknown) => {
      if (failure instanceof ApiError && failure.status === 304) {
        if (who === account && !memory.has(spaceId) && known) keep(spaceId, known);
        return known;
      }
      if (who !== account) return undefined;
      if (failure instanceof ApiError && (failure.status === 403 || failure.status === 404)) keep(spaceId, undefined);
      else checked.delete(spaceId);
      return memory.get(spaceId);
    })
    .finally(() => { if (asking.get(spaceId) === request) asking.delete(spaceId); });
  asking.set(spaceId, request);
  return request;
}

/** The lexicon once there is one: at once when known, else the first answer. */
export function lexiconReady(spaceId: string | undefined): Promise<Lexicon | undefined> {
  const known = lexiconOf(spaceId);
  return known || !spaceId ? Promise.resolve(known) : refreshLexicon(spaceId);
}

/**
 * After a page is created or renamed or a contact saved: the space's lexicon (every space's without one) is revalidated at the next
 * idle moment if it is in use, else at its next use.
 */
export function invalidateLexicon(spaceId?: string): void {
  const spaces = spaceId ? [spaceId] : [...checked.keys()];
  for (const id of spaces) {
    checked.delete(id);
    if (memory.has(id)) lexiconOf(id);
  }
}

/** The space's lexicon for a component: the last known at once, re-read on every answer; a change of space revalidates the new one. */
export function useLexicon(spaceId: MaybeRefOrGetter<string | undefined>) {
  watch(() => toValue(spaceId), id => { if (id) { invalidateLexicon(id); lexiconOf(id); } });
  return computed(() => (revision.value, lexiconOf(toValue(spaceId))));
}

/** The place's lexicon as an editor reads it (controller option and MarkdownEditor prop `lexicon`): from memory at once, a change re-read. */
export function lexiconSource(spaceId: MaybeRefOrGetter<string | undefined>): MarkdownLexiconSource {
  return { read: () => lexiconOf(toValue(spaceId)), subscribe: onChange => watch(useLexicon(spaceId), () => onChange()) };
}
