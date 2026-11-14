import { objectPayload, readObjectPayload, type DraggedObject } from "./boards/boardDrop";

/**
 * What a press inside a page's text drags into the space tree as a reference node (spec 2026-10-27 §13.1, 3; reading and editing
 * alike) or onto a board as cards (spec 2026-10-30 §8): a contact of a contact card or mention, by the `objectId` of its snapshot
 * (§13.3; a contact without one is linked first, `linkedId`), the whole list when its block is selected, or a requirement block,
 * as its page with the block's id for the anchor. Checked as the tree reads it.
 */
export interface ContactSnapshot { readonly name?: string; readonly objectId?: string }
export interface ContactSnapshots { readonly contacts?: readonly ContactSnapshot[] }

/** The contact of one `.md-contact-card` of a contacts node whose data is `data`: its index among the node's cards. */
export function contactAt(card: Element, data: ContactSnapshots | undefined): ContactSnapshot | undefined {
  const root = card.closest(".md-contact-list, .md-contact-inline");
  return root ? data?.contacts?.[[...root.querySelectorAll(".md-contact-card")].indexOf(card)] : undefined;
}

const objectOf = (contact: ContactSnapshot | undefined, id = contact?.objectId) =>
  id ? checked({ id, typeId: "atlas.contact", ...(contact?.name && { title: contact.name }) }) : undefined;

/** `card`'s contact, by its `objectId` or the one it was linked to; without the document's data (a read view has no editor) the one
 * the rendered card names (`data-object-id`); undefined without any. */
export function contactObject(card: Element, data: ContactSnapshots | undefined, linkedId?: string): DraggedObject | undefined {
  const contact = contactAt(card, data);
  if (contact) return objectOf(contact, contact.objectId ?? linkedId);
  const { objectId, contactName } = (card as HTMLElement).dataset ?? {};
  return data ? undefined : objectOf(contactName ? { name: contactName } : undefined, objectId);
}

/** Every contact of the node that has an `objectId` (its block selected: the whole list drags, several cards on a board). */
export const contactObjects = (data: ContactSnapshots | undefined): DraggedObject[] => (data?.contacts ?? []).flatMap(contact => objectOf(contact) ?? []);

const checked = (object: DraggedObject) => readObjectPayload(objectPayload(object));
