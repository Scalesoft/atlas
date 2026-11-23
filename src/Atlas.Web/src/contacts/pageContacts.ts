import { downloadMarkdownFile } from "@atlas/markdown/core";
import { locale } from "../i18n";
import t from "../i18n/contacts";
import { serializeVCard, type ContactsData, type MarkdownContact } from "@atlas/markdown/plugins/contacts";
import { eachNode, type OverviewDocument, type OverviewJson, type OverviewLevel } from "../overview/overviewKinds";
import { contactApi } from "./contactApi";

const contactNodes = new Set(["contactCard", "contactInline"]);
const collator = new Intl.Collator(locale);

/**
 * The people of a document (a page, a board's contact blocks): its contact cards and mentions, each person once (by `objectId`, a
 * contact without one by its name and first e-mail), grouped by organization (A–Z, the people without one last), in document order
 * within a group. A template's empty place and an unresolved name are no person yet.
 */
export function pageContacts(document: OverviewDocument): MarkdownContact[] {
  const people = new Map<string, MarkdownContact>();
  const add = (type: string | undefined, attrs: Readonly<Record<string, unknown>> | undefined) => {
    if (!contactNodes.has(type ?? "")) return;
    for (const contact of (attrs?.data as ContactsData | null | undefined)?.contacts ?? []) {
      const key = contact.objectId?.toLowerCase() ?? `${contact.name}|${contact.emails?.[0] ?? ""}`;
      if (!contact.unresolved && contact.name && !people.has(key)) people.set(key, contact);
    }
  };
  eachNode(document, add);
  return [...people.values()].sort((a, b) => (!a.organization === !b.organization ? collator.compare(a.organization ?? "", b.organization ?? "") : a.organization ? -1 : 1));
}

/** Every e-mail of the people once, in their order: the line to paste into a mail's recipients. */
export const contactEmails = (contacts: readonly MarkdownContact[]) => [...new Set(contacts.flatMap(contact => contact.emails ?? []))].join(", ");

/** The people as one vCard file (`kontakty.vcf`, one card per person; one person's file has their name, as the editor's card saves it). */
export function downloadContacts(contacts: readonly MarkdownContact[], ownerDocument: Document = document) {
  const name = contacts.length === 1 ? `${contacts[0]!.name.replace(/[^\p{L}\p{N}_-]/gu, "_")}.vcf` : "kontakty.vcf";
  downloadMarkdownFile(ownerDocument, name, new Blob([serializeVCard(contacts)], { type: "text/vcard;charset=utf-8" }));
}

/** The levels of the page's contact cards, less → more (S, M, L); a page starts at today's full card: `data-overview-contacts`, contactOverview.css. */
export const contactLevels: readonly OverviewLevel[] = [
  { id: "names", get label() { return t.levels.names; }, icon: "list", short: "S" },
  { id: "cards", get label() { return t.levels.cards; }, icon: "user", short: "M" },
  { id: "full", get label() { return t.levels.full; }, icon: "users", short: "L" },
];
export const defaultContactLevel = "full";

const mentions = /\]\(a-contact:\?([^)\s]*)\)/g;
/** The board's items that can hold a person (`boardReads`): its blocks, the directory's contact cards and texts that mention one. */
export const carriesContacts = (item: { kind: string; text?: string | null; props?: Readonly<Record<string, unknown>> | null }) =>
  item.kind === "block" || (item.kind === "object" && item.props?.typeId === "atlas.contact") || !!item.text?.includes("](a-contact:?");

/**
 * What a board's items other than its blocks stand for in its contacts overview (`boardNodes`, overviewKinds.ts): a contact card of the
 * directory and a person a text mentions (`[Jméno](a-contact:?…objectId=…)`), each as a contact block of its live contact. A person a
 * block of the board shows already is not asked for; one the reader cannot see is left out.
 */
export async function boardContactNodes(items: readonly { kind: string; text?: string | null; props?: Readonly<Record<string, unknown>> | null }[]): Promise<OverviewJson[]> {
  const ids = new Set<string>(), blocks: OverviewJson[] = [];
  for (const item of items) {
    if (item.kind === "object" && item.props?.typeId === "atlas.contact" && typeof item.props.objectId === "string") ids.add(item.props.objectId.toLowerCase());
    if (item.kind === "block" && item.props?.block) blocks.push(item.props.block as OverviewJson);
    for (const [, query] of item.text?.matchAll(mentions) ?? []) {
      const id = new URLSearchParams(query).get("objectId");
      if (id) ids.add(id.toLowerCase());
    }
  }
  const known = new Set(pageContacts({ content: blocks }).map(contact => contact.objectId?.toLowerCase()));
  const live = await Promise.all([...ids].filter(id => !known.has(id))
    .map(id => contactApi.live(id).then(found => (found.state === "ok" ? found.contact : null), () => null)));
  return live.filter(contact => !!contact).map(contact => ({ type: "contactCard", attrs: { data: { version: 1, contacts: [contact] } } }));
}
