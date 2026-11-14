import { formatAgo } from "../formatDate";
import t from "../i18n/projector";
import { lexiconReady } from "../linking/lexicon";
import type { RequirementChangeInfo, RequirementPrerequisite } from "./projectorApi";
import { crossPage, requiredKeys, type PageRequirement } from "./requirementModel";
import type { RuntimeStorage } from "./requirementRuntime";
import { requirementDefaults, type RequirementDirection, type RequirementState } from "./requirementTypes";

/** A block of another page the page requires (`key@pageId`), as an item of the page's index; `remote` is what the server told of it. */
export interface RemoteRequirement extends PageRequirement { readonly remote: RequirementPrerequisite }

/** What a page's blocks link outside the document (plan 3.1 M4b), as the server answers for this reader; nothing of it is stored. */
export interface RequirementLinks {
  /** The live names of the contacts the parties are, by contact id. */
  readonly names: ReadonlyMap<string, string>;
  /** Their logos or photos (package G), by contact id; a contact without one is not here. */
  readonly photos: ReadonlyMap<string, string>;
  /** Who changed each block last and when, by block id. */
  readonly changes: ReadonlyMap<string, RequirementChangeInfo>;
  /** The blocks of other pages the page requires, by reference. */
  readonly remote: ReadonlyMap<string, RemoteRequirement>;
  /** The space's pages by title (the linking engine's lexicon), for „Atlas › Stránka“ in „kde uloženo“; asked only by a page that has one. */
  readonly pages: readonly { id: string; title: string }[];
}

export const noLinks: RequirementLinks = { names: new Map(), photos: new Map(), changes: new Map(), remote: new Map(), pages: [] };

/** A prerequisite of another page as an item of the index: its state as read; one the reader may not read is unknown and waits (planned). */
export function remoteItem(found: RequirementPrerequisite): RemoteRequirement {
  return {
    id: found.reference, title: found.readable ? found.title || found.key : t.links.unavailable, ancestors: [], remote: found,
    attrs: { ...requirementDefaults, key: found.reference, state: (found.readable && found.state ? found.state : "planned") as RequirementState,
      direction: (found.direction ?? "internal") as RequirementDirection },
  };
}

/** „Upravil Jana Nováková, před 2 hodinami“; a publication says who made the version it published. */
export const changedText = (changed: RequirementChangeInfo, now = Date.now()) => t.links.changed(changed.changedBy || t.links.unknownAuthor, formatAgo(changed.changedAt, now));

/** How long after the page changed its history is asked again: the server records it when the page is saved. */
export const historyDelayMs = 15_000;

/**
 * Keeps `storage.links` of one editor with the server, through its host: the names of the contacts its blocks' parties are, the
 * prerequisites of other pages they require (asked again when either set changes), and who changed each block (when the editor
 * starts and a while after the page changed). `changed` is told when anything new arrived. Without a host, or a page, nothing.
 */
export function linkLoader(storage: RuntimeStorage, changed: () => void) {
  let contacts = "", refs = "", pages = false, asked = false, timer: ReturnType<typeof setTimeout> | undefined, destroyed = false;
  const set = (links: Partial<RequirementLinks>) => {
    if (destroyed) return;
    storage.links.value = { ...storage.links.value, ...links };
    changed();
  };
  async function history() {
    const host = storage.host, pageId = host?.pageId();
    // A page without requirements has no history of them to ask for; the first one to arrive asks (sync).
    if (!host?.api.pageRequirements || !pageId || !storage.index.value.items.length) return;
    asked = true;
    const blocks = await host.api.pageRequirements(pageId).then(page => page.blocks, () => null);
    if (blocks) set({ changes: new Map(blocks.flatMap(block => (block.changed ? [[block.blockId, block.changed] as const] : []))) });
  }
  function sync() {
    const host = storage.host, items = storage.index.value.items;
    if (!host) return;
    if (!asked && items.length) void history();
    const ids = [...new Set(items.map(item => item.attrs.partyContactId).filter(Boolean))].sort();
    if (host.contact && ids.join() !== contacts) {
      contacts = ids.join();
      void Promise.all(ids.map(async id => [id, await host.contact!(id)] as const))
        .then(found => set({ names: new Map(found.flatMap(([id, contact]) => (contact ? [[id, contact.name] as const] : []))),
          photos: new Map(found.flatMap(([id, contact]) => (contact?.photo ? [[id, contact.photo] as const] : []))) }));
    }
    const wanted = [...new Set(items.flatMap(item => requiredKeys(item.attrs)).filter(key => crossPage(key)))].sort();
    if (host.api.prerequisites && wanted.join() !== refs) {
      refs = wanted.join();
      if (!wanted.length) set({ remote: new Map() });
      else void host.api.prerequisites(wanted).then(found => set({ remote: new Map(found.map(entry => [entry.reference, remoteItem(entry)])) }), () => undefined);
    }
    // The space's pages once, for the first „Atlas › …“ a block keeps.
    if (!pages && items.some(item => item.attrs.where.includes("Atlas"))) {
      pages = true;
      void lexiconReady(host.spaceId()).then(lexicon => { if (lexicon?.pages.length) set({ pages: lexicon.pages }); });
    }
  }
  return {
    sync,
    /** The page changed: its history is asked again once it has been quiet for a while. */
    touched() {
      clearTimeout(timer);
      timer = setTimeout(() => void history(), historyDelayMs);
    },
    start: sync,
    destroy() { destroyed = true; clearTimeout(timer); },
  };
}
