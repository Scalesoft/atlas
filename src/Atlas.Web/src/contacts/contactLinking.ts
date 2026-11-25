import type { ContactsPluginOptions, LiveContact, MarkdownContact } from "@atlas/markdown/plugins/contacts";
import t from "../i18n/contacts";
import type { Editor } from "@tiptap/core";
import { h, shallowRef } from "vue";
import { ApiError, errorMessage } from "../api";
import ContactMatchPanel from "../components/contacts/ContactMatchPanel.vue";
import { useToast } from "../composables/useToast";
import { lazyComponent } from "../lazyComponent";
import { objectApi } from "../objects/objectApi";
import { usageApi, type UsageApi, type UsageCount, type UsagePage } from "../objects/relationsApi";
import { openVuePopover, openVueReaderPopover } from "../projector/openVuePopover";
import skillsText from "../i18n/contacts-ui";
import { shownLevel, skillApi, type ContactSkill } from "./skillApi";
import { buildShellHashEncoded } from "../shellRoute";
import { contactApi, linkedSnapshot, type ContactCandidate, type ContactConflict, type ContactPlace } from "./contactApi";
import type { ContactPatch } from "./contactData";
import { drawContactQr } from "./contactQr";
import { themedUserColor } from "../live/collaboratorColors";

/**
 * Every person put in a page or on a board is a reference to one directory contact (spec 2026-10-30 §3, §7): a pick is matched first
 * (`/contacts/match`), the person decides what the match asks (ContactMatchPanel), and the contact resolves to its `objectId`
 * (`/contacts/resolve`). Removing the last place a contact is used offers to trash it (K2).
 */
export type ContactDecision =
  | { kind: "exists"; name: string; contactId: string; usedIn: ContactPlace[] }
  | { kind: "weak"; name: string; candidates: ContactCandidate[] }
  | { kind: "conflict"; name: string; contactId: string; usedIn: ContactPlace[]; conflicts: ContactConflict[]; canEdit: boolean };
/** The directory contact to link (none: a new one) and the snapshot's fields that are to hold in the contact („nové“). */
export interface ContactAnswer { contactId?: string; fields?: ContactPatch }
/** Asks the person; null: they went back. */
export type ContactAsk = (decision: ContactDecision) => Promise<ContactAnswer | null>;

/** Read when shown, not at import, so it follows the page language. */
export const repeatedContact = () => t.linking.repeated;

/** What a match asks of the person, or nothing (no match: a new contact). Pure. */
export function decisionOf(contact: MarkdownContact, match: { strong?: ContactCandidate | null; weak?: ContactCandidate[]; conflicts?: ContactConflict[] }): ContactDecision | null {
  const strong = match.strong;
  if (strong) {
    const base = { name: strong.name || contact.name, contactId: strong.contactId, usedIn: strong.usedIn ?? [] };
    return match.conflicts?.length ? { kind: "conflict", ...base, conflicts: match.conflicts, canEdit: strong.canEdit !== false } : { kind: "exists", ...base };
  }
  return match.weak?.length ? { kind: "weak", name: contact.name, candidates: match.weak } : null;
}

const forbidden = (failure: unknown) => failure instanceof ApiError && (failure.status === 403 || failure.code === "contact_forbidden");

/**
 * The snapshot to insert, linked to its directory contact; null when the person went back. With `ask` a match is asked about first;
 * without it (the plugin's `link`) the server decides (§2 UpsertAsync). Who may not add to the wiki keeps the snapshot unlinked.
 */
export async function linkContact(contact: MarkdownContact, ask?: ContactAsk, services: { contacts: Pick<typeof contactApi, "match" | "resolve" | "forget">; objects: Pick<typeof objectApi, "change"> } = { contacts: contactApi, objects: objectApi }): Promise<MarkdownContact | null> {
  // An unresolved name (several people, nobody picked) is nobody yet: resolving it would create one more contact of that name.
  if (contact.objectId || contact.unresolved) return contact;
  try {
    let answer: ContactAnswer = {};
    if (ask) {
      const decision = decisionOf(contact, await services.contacts.match(contact));
      if (decision) {
        const given = await ask(decision);
        if (!given) return null;
        answer = given;
      }
    }
    const resolved = await services.contacts.resolve(contact, answer.contactId);
    const fields = answer.fields && Object.keys(answer.fields).length ? answer.fields : undefined;
    if (fields) { await services.objects.change(resolved.contactId, { fields }); services.contacts.forget(resolved.contactId); }
    return linkedSnapshot(contact, resolved.contactId, { ...resolved.contact, ...fields });
  } catch (failure) {
    if (forbidden(failure)) return contact;
    throw failure;
  }
}

/** How many contacts of a document (its contact cards and mentions, and a requirement heading's party contact) are this directory contact. */
export function contactsIn(doc: { descendants(visit: (node: { type: { name: string }; attrs: Record<string, unknown> }) => boolean | void): void }, objectId: string): number {
  let count = 0;
  const id = objectId.toLowerCase();
  doc.descendants(node => {
    if (node.type.name === "heading" && String(node.attrs.partyContactId ?? "").toLowerCase() === id) count++;
    if (node.type.name !== "contactCard" && node.type.name !== "contactInline") return;
    const contacts = (node.attrs.data as { contacts?: { objectId?: string }[] } | null)?.contacts ?? [];
    count += contacts.filter(contact => contact.objectId?.toLowerCase() === id).length;
    return false;
  });
  return count;
}

/** No place but `here` uses the contact, and `here` no longer does (`stillHere` other references left): the usage service (ObjectUsage,
 * drafts counted) finds no use in a place the reader may not see and at most one they may, and that one is `here` (a page not yet saved,
 * a board whose deletion arrived first, count none). `places` lists the visible uses, asked for only when `total` is 1. Pure. */
export function isLastUsage(count: Pick<UsageCount, "total" | "hidden">, here: Pick<ContactPlace, "id">, stillHere: number, places?: Pick<UsagePage, "items">): boolean {
  if (stillHere > 0 || count.hidden || count.total > 1) return false;
  return count.total === 0 || (!!places && places.items.every(use => use.placeId.toLowerCase() === here.id.toLowerCase()));
}

/** The open „poslední použití“ question (ContactLastUsageDialog, mounted once by the app); one at a time. */
export const lastUsagePrompt = shallowRef<{ name: string; answer: (trash: boolean) => void } | null>(null);
let prompts = Promise.resolve();
export function askLastUsage(name: string): Promise<boolean> {
  const asked = prompts.then(() => new Promise<boolean>(settle => { lastUsagePrompt.value = { name, answer: trash => { lastUsagePrompt.value = null; settle(trash); } }; }));
  prompts = asked.then(() => undefined);
  return asked;
}

/**
 * A reference to a directory contact left `here` (K2): when no other place uses it, the person decides whether it goes to the
 * object trash („Přesunout do koše“) or stays unused. A usage that cannot be read (or a contact no longer readable) keeps the contact.
 */
export async function contactRemoved(objectId: string, name: string, here: Pick<ContactPlace, "kind" | "id">, stillHere: number,
  services: { usage: Pick<UsageApi, "count" | "usage">; contacts: Pick<typeof contactApi, "forget">; objects: Pick<typeof objectApi, "trash">; ask: (name: string) => Promise<boolean> } = { usage: usageApi, contacts: contactApi, objects: objectApi, ask: askLastUsage }): Promise<"trashed" | "kept"> {
  if (stillHere > 0) return "kept";
  let last: boolean;
  try {
    const count = await services.usage.count(objectId, undefined, undefined, true);
    last = !!count && isLastUsage(count, here, stillHere, count.total === 1 ? (await services.usage.usage(objectId)) ?? undefined : undefined);
  } catch { return "kept"; }
  if (!last || !(await services.ask(name))) return "kept";
  try {
    await services.objects.trash(objectId);
    services.contacts.forget(objectId);
    return "trashed";
  } catch (failure) {
    useToast().error(t.linking.deleteFailed(name, errorMessage(failure)));
    return "kept";
  }
}

/** „Zpět“ on a card deletion after its contact went to the trash: the contact comes back, so the restored card points to a live one. */
export async function contactsRestored(objectIds: readonly string[], services: { contacts: Pick<typeof contactApi, "forget">; objects: Pick<typeof objectApi, "restore"> } = { contacts: contactApi, objects: objectApi }) {
  for (const id of objectIds) {
    try { await services.objects.restore(id); services.contacts.forget(id); }
    catch (failure) { useToast().error(t.linking.restoreFailed(errorMessage(failure))); }
  }
}

/** Where an editor's contacts are: the place (the page) and how many references to a contact its document holds now. */
export interface ContactHost { place(): Pick<ContactPlace, "kind" | "id"> | undefined; count(objectId: string): number }

/**
 * What a match asks where a contact goes in through the editor plugin (`@`, its contact form, „Přidat z adresáře“): ContactMatchPanel in the
 * editor's popover at that place, the panel /Kontakt's picker shows; closing the popover goes back.
 */
export function askAt(insert: { editor: Editor; anchor: () => DOMRect }): ContactAsk {
  return decision => new Promise(settle => {
    let answered = false;
    const close = openVuePopover({ editor: insert.editor, anchor: insert.editor.view.dom, positionAnchor: { getBoundingClientRect: insert.anchor }, title: t.linking.contactNamed(decision.name),
      variant: "panel", closed: () => { if (!answered) settle(null); },
      component: () => h(ContactMatchPanel, { decision, onAnswer: (answer: ContactAnswer | null) => { answered = true; settle(answer); close(); } }) });
  });
}

/** The directory contact's editor on a page; loaded with the first edit (ContactCard brings the board text editor). */
const PageContactEditor = lazyComponent(() => import("../components/contacts/PageContactEditor.vue"));

/** The rating of a person's topics, loaded with its first use (skills spec §7). */
const SkillRatePanel = lazyComponent(() => import("../components/contacts/SkillRatePanel.vue"));
const readerTopics = 6;
/** Who else knows a topic: the contact directory searched for it (`#/library/contacts?q=`). */
export const topicUrl = (topic: string) => buildShellHashEncoded({ view: "library", tab: "contacts", q: topic });
/** One topic in the person's card: the level the reader may see and a line on who says so and how. */
function readerSkill(skill: ContactSkill) {
  const t = skillsText.skills;
  const who = skill.peers ? t.confirmedShort(skill.peers, skill.peersAtLeast) : skill.fewPeers ? t.fewShort : skill.self != null ? t.selfShort : skill.mine != null ? t.yours(skill.mine) : "";
  return { name: skill.name, level: shownLevel(skill), detail: [who, skill.selfNote ?? "", skill.myNote ?? "", ...skill.raters.map(rater => `${rater.name}: ${rater.level}${rater.note ? ` (${rater.note})` : ""}`)].filter(Boolean).join(" · "), url: topicUrl(skill.name) };
}
/** „Umí“ of the read-only card: the topics the reader may see, the best first; null for a contact the reader may not read. */
export async function readerSkills(objectId: string, signal: AbortSignal, api: Pick<typeof skillApi, "contact"> = skillApi) {
  try {
    const skills = (await api.contact(objectId, signal)).skills;
    return { skills: skills.slice(0, readerTopics).map(readerSkill), more: Math.max(0, skills.length - readerTopics) };
  } catch (failure) {
    if (failure instanceof ApiError && failure.status === 404) return null;
    throw failure;
  }
}
/** Ohodnotit of the card: the rating panel at the person's chip, a reader's panel, so it works in a read page as in the editor. */
export function rateSkills(objectId: string, anchor: HTMLElement, api?: Pick<typeof skillApi, "contact" | "rate" | "topics">, topic?: string) {
  return openVueReaderPopover({ anchor, root: anchor.closest<HTMLElement>(".ProseMirror, [data-md-root]") ?? anchor.ownerDocument.body, title: skillsText.skills.rateTitle(anchor.textContent?.trim() ?? ""),
    className: "skill-rate-popover", component: SkillRatePanel, props: { contactId: objectId, api, topic } });
}

const usedOnPlaces = 5;
/** The places (pages and boards the reader may see) that use a contact, for the read-only card; null when the contact is unreadable. */
/** `here`: the page being read, which is not listed as a place of its own contact. */
export async function usedOn(objectId: string, signal: AbortSignal, api: Pick<UsageApi, "usage"> = usageApi, here?: string) {
  const page = await api.usage(objectId, null, signal);
  if (!page) return null;
  const seen = new Set<string>();
  const places = page.items.filter(row => row.placeId !== here && !seen.has(row.placeId) && seen.add(row.placeId)).map(row => ({
    title: row.placeTitle, ...(row.spaceName ? { detail: row.spaceName } : {}),
    url: buildShellHashEncoded(row.placeTypeId === "atlas.board" ? { view: "board", boardId: row.placeId } : { view: "page", pageId: row.placeId }) }));
  return { places: places.slice(0, usedOnPlaces), more: Math.max(0, places.length - usedOnPlaces) };
}

/** A contact linked to an account (its `directoryId` is the account's id, AccountContacts) is that internal person: the editor colours their
 *  avatar as ContactCard does. */
export const withAccount = (live: LiveContact): LiveContact => live.state === "ok" && live.contact.directoryId && !live.contact.internalUser
  ? { ...live, contact: { ...live.contact, internalUser: { id: live.contact.directoryId, name: live.contact.name } } } : live;

/** The contacts plugin's host options (§3, §4, K2): live contacts always; linking and the last-usage question where the document is a place. */
export function contactPluginOptions(host?: ContactHost): Pick<ContactsPluginOptions, "live" | "link" | "remove" | "directoryUrl" | "editLinked" | "readerUsage" | "qrCode" | "readerSkills" | "rateSkills" | "userColor"> {
  return {
    qrCode: drawContactQr,
    // A person's chip in their caret's colour (one colour per person in pages, boards and history).
    userColor: themedUserColor,
    live: (objectId, { signal }) => contactApi.live(objectId, { signal }).then(withAccount),
    editLinked: (contact, { editor, anchor, signal, save }) =>
      openVuePopover({ editor, anchor, signal, title: `Kontakt ${contact.name}`, variant: "panel", component: PageContactEditor, props: { snapshot: contact, save } }),
    // The contact's dock over the directory: its card, and a colleague's activity.
    directoryUrl: objectId => buildShellHashEncoded({ view: "library", tab: "contacts", contact: objectId }),
    // „Použito na“ of the read-only card: the first few places the reader may see, the rest as a number.
    readerUsage: (objectId, { signal }) => usedOn(objectId, signal, undefined, host?.place()?.id),
    // „Umí“ of the read-only card and its Ohodnotit (Mapa znalostí).
    readerSkills: (objectId, { signal }) => readerSkills(objectId, signal),
    rateSkills: (objectId, { anchor }) => { rateSkills(objectId, anchor); },
    ...(host && {
      link: async (contact, { insert }) => {
        // An unresolved name waits for a person to be picked; never resolved or created by itself.
        if (!insert) { if (contact.unresolved) return null; const linked = await linkContact(contact); return linked?.objectId ? linked : null; }
        // A person put in now is asked about as /Kontakt asks: the match first, then a contact the page has already is said.
        const linked = await linkContact(contact, askAt(insert));
        if (linked?.objectId && host.count(linked.objectId)) useToast().warning(repeatedContact());
        return linked;
      },
      remove: async contact => {
        const place = host.place();
        // Counted before the plugin removes it: the one being removed is still in the document.
        if (contact.objectId && place) void contactRemoved(contact.objectId, contact.name, place, host.count(contact.objectId) - 1);
        return true;
      },
    }),
  };
}
