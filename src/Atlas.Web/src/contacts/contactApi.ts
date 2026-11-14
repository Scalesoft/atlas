import type { LiveContact, MarkdownContact } from "@atlas/markdown/plugins/contacts";
import { api, post } from "../api";
import { contactProps } from "../boards/boardTargets";
import type { ContactData } from "./contactData";

/**
 * The contact as one entity: resolve a snapshot to its directory contact, match without
 * writing, the live contact behind an `objectId` (batched, cached), where a contact is used, possible duplicates, merge, and the
 * admin's linking of the contacts written in pages. JSON camelCase; a refusal is an ApiError (`contact_forbidden`, `invalid_contact`).
 */
export type ContactPlace = { kind: "page" | "board"; id: string; title: string };
/** One shape for the web and MCP (§2). */
export interface ContactWarning {
  type: "contact_exists" | "contact_conflict" | "possible_duplicate" | "contact_missing";
  contactId: string; name: string; field?: string; existing?: unknown; new?: unknown; usedIn?: ContactPlace[]; index?: number;
}
export interface ContactConflict { field: string; existing: unknown; new: unknown }
/** A directory contact a match or the duplicates found (server ContactRef); `canEdit` is not sent yet, so "nové" is offered and a
 * refused change answers 403. */
export interface ContactCandidate { contactId: string; name: string; contact?: ContactData; usedIn?: ContactPlace[]; canEdit?: boolean }
export interface ContactMatch { strong?: ContactCandidate | null; weak: ContactCandidate[]; conflicts: ContactConflict[] }
export interface ContactResolved { contactId: string; created: boolean; contact: MarkdownContact; warnings: ContactWarning[] }
export interface ContactPair { a: ContactCandidate; b: ContactCandidate; reason: string }
/** An account and a directory contact that may be one person (server AccountContactSuggestion, wiki admins only): `reason` name (the same
 *  name) or email (one of several contacts of the account's address); `ownContactId` the contact the account has meanwhile, merged on link. */
export interface AccountContactSuggestion { accountId: string; accountName: string; accountEmail?: string | null; accountActive: boolean; contact: ContactCandidate; reason: string; ownContactId?: string | null; accountUsername?: string | null }
/** One person of the migration (server ContactLinkItem): its snapshots of one identity, the pages they are on, why it is unclear, and
 *  `spaceId` when it is created as a contact of that space (no page every wiki reader reads holds it). */
export interface ContactLinkItem { name: string; identityKey: string; occurrences: number; pageIds: string[]; contactId?: string | null; reason?: string | null; spaceId?: string | null }
/** `restricted`: people without a match written only on pages with their own access, drafts or templates; no contact is made from them (absent in older runs). */
export interface ContactLinkReport { mode: "report" | "apply"; linked: ContactLinkItem[]; created: ContactLinkItem[]; unclear: ContactLinkItem[]; restricted?: ContactLinkItem[] | null; pages: number; revisions: number }
/** The background run of apply (server ContactLinkRunView, a run of the job contacts.link; `id` is its JobRun): `report` is what its plan decided (empty while it plans),
 * `pagesDone` of `pages` its progress. */
export interface ContactLinkRun { id: number; state: "queued" | "running" | "succeeded" | "failed" | "cancelled"; startedAt: string; finishedAt?: string | null; pages: number; pagesDone: number; revisions: number; report: ContactLinkReport }
type LiveItem = { state: "ok" | "hidden" | "trashed" | "missing"; contactId?: string; contact?: unknown };

const path = (id: string) => `/contacts/${encodeURIComponent(id)}`;

/** The snapshot of a resolved or live contact: the directory's fields, the page's own role, kind and account kept (§3, §4). */
export function linkedSnapshot(snapshot: MarkdownContact, contactId: string, directory?: unknown): MarkdownContact {
  const { role, kind, internalUser, username } = snapshot;
  return { ...snapshot, ...(contactProps(directory) ?? {}), ...(role && { role }), ...(kind && { kind }), ...(internalUser && { internalUser }), ...(username && { username }), objectId: contactId };
}

const liveTtlMs = 30_000, liveBatch = 100;
interface Waiting { id: string; settle: (live: LiveContact) => void; fail: (error: unknown) => void }

/** The client; `request` is replaced by the tests. */
export function createContactApi(request: { get: <T>(path: string, signal?: AbortSignal) => Promise<T>; post: <T>(path: string, body: unknown) => Promise<T> } = {
  get: (url, signal) => api(url, { signal }), post: (url, body) => post(url, body),
}) {
  // shortcut: no live signal reaches pages yet, so the cache only lives 30 s and forgets what this tab changed; subscribe when one does.
  const cache = new Map<string, { at: number; live: Promise<LiveContact> }>();
  let waiting: Waiting[] = [];
  async function flush(batch: Waiting[]) {
    const ids = [...new Set(batch.map(entry => entry.id))];
    for (let start = 0; start < ids.length; start += liveBatch) {
      const chunk = ids.slice(start, start + liveBatch);
      const asking = batch.filter(entry => chunk.includes(entry.id));
      try {
        const { items } = await request.post<{ items: Record<string, LiveItem> }>("/contacts/live", { ids: chunk });
        const byId = new Map(Object.entries(items ?? {}).map(([id, item]) => [id.toLowerCase(), item]));
        for (const entry of asking) entry.settle(liveOf(byId.get(entry.id)));
      } catch (error) {
        for (const entry of asking) { cache.delete(entry.id); entry.fail(error); }
      }
    }
  }
  const liveOf = (item: LiveItem | undefined): LiveContact => {
    const contact = item?.state === "ok" ? contactProps(item.contact) : null;
    // A merged contact answers with its target (`contactId`): the reference follows it.
    if (contact) return { state: "ok", contact: { ...contact, objectId: item!.contactId ?? contact.objectId } };
    return { state: item && item.state !== "ok" ? item.state : "missing" };
  };
  function queue(id: string) {
    return new Promise<LiveContact>((settle, fail) => {
      if (!waiting.length) queueMicrotask(() => { const batch = waiting; waiting = []; void flush(batch); });
      waiting.push({ id, settle, fail });
    });
  }
  const forget = (id?: string) => (id ? cache.delete(id.toLowerCase()) : cache.clear());
  return {
    /** Answers that came with the page (`GET /pages/{id}?bundle=true`), so its contacts ask nothing for 30 s. */
    seed(items: Record<string, LiveItem>) {
      const at = Date.now();
      for (const [id, item] of Object.entries(items)) cache.set(id.toLowerCase(), { at, live: Promise.resolve(liveOf(item)) });
    },
    /** The live contact behind `objectId`: every id asked for in one tick goes in one request, an answer is kept for 30 s. */
    live(objectId: string, { signal }: { signal?: AbortSignal } = {}): Promise<LiveContact> {
      const id = objectId.toLowerCase(), known = cache.get(id);
      const live = known && Date.now() - known.at < liveTtlMs ? known.live : queue(id);
      if (live !== known?.live) cache.set(id, { at: Date.now(), live });
      if (!signal) return live;
      return new Promise((settle, fail) => {
        if (signal.aborted) return fail(signal.reason);
        signal.addEventListener("abort", () => fail(signal.reason), { once: true });
        live.then(settle, fail);
      });
    },
    forget,
    resolve: (contact: MarkdownContact, contactId?: string) => request.post<ContactResolved>("/contacts/resolve", { contact, ...(contactId && { contactId }) }),
    match: (contact: MarkdownContact) => request.post<ContactMatch>("/contacts/match", { contact }),
    duplicates: (signal?: AbortSignal) => request.get<{ pairs: ContactPair[] }>("/contacts/duplicates", signal),
    async merge(sourceId: string, targetId: string) {
      const result = await request.post<{ contactId: string }>("/contacts/merge", { sourceId, targetId });
      forget(sourceId); forget(targetId);
      return result;
    },
    /** For a wiki admin the open account–contact suggestions; empty for anyone else. */
    accountSuggestions: (signal?: AbortSignal) => request.get<{ items: AccountContactSuggestion[] }>("/contacts/accounts", signal),
    /** link: the contact becomes the account's (its own one merged in); dismiss: „Není to on“, the account gets its own contact
     * (createdContactId), so it is never offered again; restore with that createdContactId undoes it (the contact goes to the trash). */
    async resolveAccount(accountId: string, contactId: string, decision: "link" | "dismiss" | "restore") {
      const result = await request.post<{ mergedContactId?: string | null; createdContactId?: string | null }>("/contacts/accounts", { accountId, contactId, decision });
      forget(contactId); if (result.mergedContactId) forget(result.mergedContactId);
      return result;
    },
    linkReport: () => request.post<ContactLinkReport>("/admin/contacts/link?mode=report", undefined),
    /** Starts the background run (or answers the one running). */
    linkApply: () => request.post<ContactLinkRun>("/admin/contacts/link?mode=apply", undefined),
    /** The running run, else the newest; nothing before the first. */
    linkRun: async (signal?: AbortSignal) => (await request.get<ContactLinkRun | undefined>("/admin/contacts/link", signal)) ?? null,
    /** Stops the running run (what it did stays) and answers the newest. */
    linkCancel: async () => (await request.post<ContactLinkRun | undefined>("/admin/contacts/link?mode=cancel", undefined)) ?? null,
  };
}
export type ContactApi = ReturnType<typeof createContactApi>;

/** The tab's one client. */
export const contactApi = createContactApi();
