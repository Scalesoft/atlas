import { contactTypeId } from "../contacts/contactData";
import { neighboursOf, plainTitle, relationsApi, usageApi, type ContentRow, type RelationsApi, type UsageApi } from "../objects/relationsApi";
import { overviewApi, type WaitingList } from "../projector/overview/overviewApi";
import { locale } from "../i18n";
import t from "../i18n/boards";

/**
 * The reads of the board's derived lists (`links`, P15 „Tabule ukazují vazby samy“): nothing of their own, only the relation reads
 * every other view uses, which the server answers through the reader's rights (an unreadable source is never a row). Lidé projektu
 * are the contacts the space's pages use, ranked by their uses there (Obsah sorted by „Použito“: mentions, cards and requirement
 * parties), or those one page names; Na co čekáme is Projector's list, of the space or of one page; Kde se používá is „Použito na“.
 */
export interface BoardLinksApi {
  relations: RelationsApi;
  usage: UsageApi;
  /** WaitingList's own reads. */
  waiting: { waiting: typeof overviewApi.waiting; contacts: typeof overviewApi.contacts };
}
export const boardLinksApi: BoardLinksApi = { relations: relationsApi, usage: usageApi, waiting: overviewApi };

/** One person of „Lidé projektu“: a readable contact and the number of its uses. */
export interface LinkedPerson { id: string; name: string; uses: number }

/** The most people a list shows: a board item is a glance, the space's Obsah has the rest. */
export const peopleLimit = 12;

/** The people of the space (its pages' contacts, most used first), or of one page (the contacts it names, most named first). */
export async function linkedPeople(api: BoardLinksApi, spaceId: string, pageId: string | undefined, signal?: AbortSignal): Promise<LinkedPerson[]> {
  if (pageId) {
    const graph = await api.relations.relations(pageId, 1, signal);
    const counts = new Map<string, LinkedPerson>();
    for (const { node, direction, edge } of neighboursOf(pageId, graph)) {
      if (direction !== "out" || !node?.readable || node.typeId !== contactTypeId) continue;
      const known = counts.get(node.id) ?? { id: node.id, name: plainTitle(node.title) || t.links.nameless, uses: 0 };
      counts.set(node.id, { ...known, uses: known.uses + edge.count });
    }
    return [...counts.values()].sort((a, b) => b.uses - a.uses || a.name.localeCompare(b.name, locale)).slice(0, peopleLimit);
  }
  const { items } = await api.relations.contents(spaceId, { type: contactTypeId, sort: "used" }, signal);
  return items.filter(row => row.usedCount > 0).slice(0, peopleLimit).map(row => ({ id: row.id, name: plainTitle(row.title) || t.links.nameless, uses: row.usedCount }));
}

/** Projector's waiting list kept to one page's items (a party left without any drops out). */
export const waitingOfPage = (list: WaitingList, pageId: string): WaitingList => ({
  parties: list.parties.map(party => ({ ...party, items: party.items.filter(item => item.pageId.toLowerCase() === pageId.toLowerCase()) })).filter(party => party.items.length),
});

/** What the scope picker offers: the space's pages for people and waiting, any page, board or contact for usage. */
export async function scopeChoices(api: BoardLinksApi, spaceId: string, show: "people" | "waiting" | "usage", q: string, signal?: AbortSignal): Promise<ContentRow[]> {
  const { items } = await api.relations.contents(spaceId, { ...(show === "usage" ? {} : { type: "atlas.page" }), q: q.trim(), sort: q.trim() ? "title" : "used" }, signal);
  return items;
}
