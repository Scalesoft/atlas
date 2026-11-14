import { contactSubtitle } from "../boards/boardTargets";
import t from "../i18n/projector";
import { describePeople, matchesPerson, type PersonHit } from "../contacts/contactSearch";
import { learnAlias } from "../contacts/contactSources";
import { foldText } from "../lib/foldText";

/** One suggestion for „od koho / komu / kdo“: `value` is what fills the field, `contactId` the directory contact it is, if any. */
export interface PartyRow { id: string; value: string; description: string; contactId?: string }
/** The party field's source: the rows for a text, and optionally those known at once, before any answer (P17). */
export type PartySuggest = ((text: string, signal: AbortSignal) => Promise<PartyRow[]>) & { cached?: (text: string) => PartyRow[] };

/**
 * A party picked after `typed` was written (the party field, the title's „čekáme na“), once it is applied: a directory contact may learn
 * the text as its alias („MMu“ → Marek Musil; contactSources.ts `learnAlias`, the server decides); a name of the page teaches nothing.
 */
export function learnParty(row: PartyRow, typed: string, learn: typeof learnAlias = learnAlias): void {
  if (row.contactId) learn({ contact: { name: row.value }, objectId: row.contactId }, typed);
}

/** The picker's last row: a new directory contact of the typed name (the contact picker's duplicate question comes first). */
export const newPartyId = "party:new";
export const newPartyRow = (text: string): PartyRow => ({ id: newPartyId, value: text.trim(), description: t.party.addToDirectory });

const key = (name: string) => foldText(name).replace(/\s+/g, " ").trim();
/** A person of the directory is linked by its object id (plan 3.1 §2.3: the party is the contact); anyone else stays text. */
const contact = (hit: PersonHit) => (hit.objectId ? { contactId: hit.objectId } : {});

/**
 * What the party field offers while it is typed (contactSearch.ts matching): the names this page's requirements already use, then
 * the space's („Na co čekáme“), then people from the contacts, each name once. A contact whose name is already used joins that row;
 * a linked contact always comes before unlinked text.
 * Two people of one name both stay and fill the field with what tells them apart („Jan Novák (Modelsoft)“), so a party never points
 * at the wrong one. With nothing typed only the page's names. Pure.
 */
export function partySuggestions(query: string, sources: { page: readonly string[]; space: readonly string[]; people: readonly PersonHit[] }, limit = 8): PartyRow[] {
  const typed = query.trim();
  const matches = (name: string) => !typed || matchesPerson(typed, { name });
  const rows = new Map<string, PartyRow>();
  const used = (names: readonly string[], description: string) => {
    for (const name of names) if (name.trim() && matches(name) && !rows.has(key(name))) rows.set(key(name), { id: `party:${key(name)}`, value: name.trim(), description });
  };
  used(sources.page, t.party.onThisPage);
  if (typed) {
    used(sources.space, t.party.inSpace);
    const people = sources.people.filter(hit => matchesPerson(typed, hit.contact, hit.aliases)), lines = describePeople(people), counts = new Map<string, number>();
    for (const hit of people) counts.set(key(hit.contact.name), (counts.get(key(hit.contact.name)) ?? 0) + 1);
    for (const hit of people) {
      const name = hit.contact.name, k = key(name), n = counts.get(k)!, subtitle = contactSubtitle(hit.contact);
      if (n > 1) {
        const tag = hit.contact.organization || hit.contact.emails?.[0] || hit.origin;
        rows.set(`${k}#${hit.key}`, { id: hit.key, value: `${name} (${tag})`, description: `${t.party.sameName(n)} · ${lines.get(hit.key)}`, ...contact(hit) });
      } else if (rows.has(k)) {
        const row = rows.get(k)!;
        if (subtitle) row.description += ` · ${subtitle}`;
        // A name already used becomes the directory's contact of that name (the page's text and the person are one party).
        if (!row.contactId && hit.objectId) Object.assign(row, { value: name }, contact(hit));
      } else rows.set(k, { id: hit.key, value: name, description: subtitle || t.party.contact, ...contact(hit) });
    }
  }
  // A linked contact before any unlinked text, so the first row (Enter) never spreads a text stand-in of a known person.
  const all = [...rows.values()];
  return [...all.filter(row => row.contactId), ...all.filter(row => !row.contactId)].slice(0, limit);
}
