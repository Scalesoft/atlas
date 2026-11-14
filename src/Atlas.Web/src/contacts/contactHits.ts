import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";
import { contactProps, httpUrl } from "../boards/boardTargets";

/**
 * The contacts the wiki search finds (`GET /api/search`: a hit lists the contact widgets of its page, `SearchWidgets`) as the
 * contacts of the editor's contacts block. A hit carries the name and one flattened line, "function · organization · department · login · e-mails ·
 * phones · website · address", with the empty parts left out, so the line cannot say which part was which. E-mails, phones and the
 * website are known by their shape; the rest stays, in its order, as the line under the name (`title`), since guessing function
 * from organization from login would put one person's login on the card as a company. A hit that carries the structured `contact`
 * (the server may add it, the line then is not read) is used as it is.
 */
export interface ContactWidgetLike { kind?: string; title?: string; detail?: string; contact?: unknown }
export interface SearchItemLike { title?: string; spaceName?: string; widgets?: readonly ContactWidgetLike[] | null }
/** The wiki search: the results of `GET /api/search`, whose pages list their contact widgets. */
export type ContactSearch = (text: string, signal: AbortSignal) => Promise<SearchItemLike[]>;
export interface ContactHit {
  /** Stable while the result list changes, so a pick survives the next search. */
  key: string;
  contact: MarkdownContact;
  detail: string;
  /** "Page · Space" the contact was found on. */
  source: string;
}

const email = /^[^\s@<>?,;:]+@[^\s@<>?,;:]+\.[^\s@<>?,;:]+$/u;
/** The editor's phone pattern, and at least five digits: a year or a house number in the line is no phone. */
const phone = /^\+?[\d\s().\-#*]{1,80}$/u;
const isPhone = (text: string) => phone.test(text) && (text.match(/\d/g)?.length ?? 0) >= 5;

export function contactFromWidget(widget: ContactWidgetLike): MarkdownContact | null {
  if (widget.contact && typeof widget.contact === "object") return contactProps(widget.contact);
  const emails: string[] = [], phones: string[] = [], rest: string[] = [];
  let website: string | undefined;
  for (const part of (widget.detail ?? "").split(" · ").map(text => text.trim()).filter(Boolean)) {
    if (email.test(part)) emails.push(part);
    else if (isPhone(part)) phones.push(part);
    else if (!website && httpUrl(part)) website = part;
    else rest.push(part);
  }
  return contactProps({ name: widget.title, ...(rest.length && { title: rest.join(" · ") }), emails, phones, ...(website && { website }) });
}

const maxHits = 50;

/** The contact widgets of search results, once each (the same contact written on several pages), at most 50. */
export function contactHitsOf(items: readonly SearchItemLike[]): ContactHit[] {
  const hits = new Map<string, ContactHit>();
  for (const item of items) {
    for (const widget of item.widgets ?? []) {
      if (widget.kind !== "contact") continue;
      const contact = contactFromWidget(widget);
      const key = `${widget.title}\u0000${widget.detail ?? ""}`;
      if (!contact || hits.has(key)) continue;
      hits.set(key, { key, contact, detail: widget.detail ?? "", source: [item.title, item.spaceName].filter(Boolean).join(" · ") });
      if (hits.size >= maxHits) return [...hits.values()];
    }
  }
  return [...hits.values()];
}
