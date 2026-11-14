import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";
import t from "../i18n/contacts";
import { ApiError } from "../api";
import { foldText } from "../lib/foldText";
import { validateContactForm, type ContactFormValues } from "./contactForm";

/**
 * A contact of the directory (`atlas.contact`, server `Features/Contacts/Domain/ContactFields.cs`): name, function, organization and
 * department are one line of inline Markdown, the address is Markdown on several lines, e-mails, phones, website and photo are
 * identifiers. A change names the fields it sets (`{ fields }`, null clears one) and the last writer wins per field. Pure.
 */
export const contactTypeId = "atlas.contact";
/** The server's limits of a line field and the address (ContactFields.MaxLine, MaxAddress; a test pins them). */
export const maxContactLine = 400, maxContactAddress = 4096;
export interface ContactData {
  name: string; title?: string; organization?: string; department?: string; emails?: string[]; phones?: string[];
  website?: string; address?: string; photo?: string; directoryId?: string;
  /** „Zkratky a přezdívky“ people find the contact by („MMu“, „Mára“); the directory's own, never in a page's snapshot. */
  aliases?: string[];
  /** How often each alias was picked (by folded alias); the server keeps it, no change sets it. */
  aliasPicks?: Record<string, number>;
}
export type ContactField = Exclude<keyof ContactData, "aliasPicks">;
export type ContactPatch = { [K in ContactField]?: ContactData[K] | null };
/** The server's limits of the aliases (ContactFields.MaxAlias, MaxEntries; MaxEntries holds the e-mails and phones too). */
export const maxAlias = 60, maxAliases = 20;

const fields: readonly ContactField[] = ["name", "title", "organization", "department", "emails", "phones", "website", "address", "aliases", "photo", "directoryId"];
const filled = (value: unknown) => (Array.isArray(value) ? value.length > 0 : typeof value === "string" && value !== "");

/** What the directory stores of an editor's contact (a page's, the form's): a login, the display kind and an internal identity stay
 * with the page. */
export function contactData(contact: MarkdownContact): ContactData {
  return Object.fromEntries(fields.flatMap(key => (filled(contact[key as keyof MarkdownContact]) ? [[key, contact[key as keyof MarkdownContact]]] : []))) as unknown as ContactData;
}

/** The fields of `after` that differ from `before`, a cleared one as null. */
export function contactPatch(before: ContactData, after: ContactData): ContactPatch {
  return Object.fromEntries(fields.flatMap(key => (JSON.stringify(before[key] ?? null) === JSON.stringify(after[key] ?? null) ? [] : [[key, after[key] ?? null]])));
}

/** The contact in the form's fields (one e-mail and one phone per line). */
export const contactFormValues = (data: ContactData): ContactFormValues => ({
  name: data.name, title: data.title ?? "", organization: data.organization ?? "", department: data.department ?? "",
  emails: (data.emails ?? []).join("\n"), phones: (data.phones ?? []).join("\n"), website: data.website ?? "", address: data.address ?? "",
});

/**
 * One field edited in place: `text` as the field's form reads it, validated by the editor's rules (`validateContactForm` with the
 * editor's `normalizeContact`), answered as the change of just that field (empty: nothing changed), or what is wrong with it.
 */
/** A contact mention written into a contact's own field (`[Jana](a-contact:?…)`) as its text: a contact never holds a reference to another one. */
export const withoutContactMentions = (text: string) => text.replace(/\[((?:\\.|[^\]\\])*)\]\(a-contact:[^)]*\)/g, (_, label: string) => label.replace(/\\([\\[\]])/g, "$1"));

export function contactFieldEdit(data: ContactData, field: keyof ContactFormValues, text: string, normalize: (value: unknown) => unknown): { patch: ContactPatch; error?: undefined } | { error: string; patch?: undefined } {
  const result = validateContactForm({ ...contactFormValues(data), [field]: withoutContactMentions(text) }, normalize);
  if (!result.contact) return { error: result.errors[field] ?? Object.values(result.errors)[0] ?? t.data.invalidField };
  const next = contactData(result.contact);
  return { patch: contactPatch({ name: data.name, [field]: data[field] }, { name: data.name, [field]: next[field] } as ContactData) };
}

/** The aliases edited as lines (ContactCard): one per line, single-spaced, each once whatever its case and diacritics (the server's rules);
 * answered as the change (empty: nothing changed; none left clears them), or what is wrong. */
export function aliasesEdit(data: ContactData, text: string): { patch: ContactPatch; error?: undefined } | { error: string; patch?: undefined } {
  const aliases: string[] = [];
  for (const line of text.split("\n").map(item => item.replace(/\s+/g, " ").trim()).filter(Boolean)) {
    if (line.length > maxAlias) return { error: t.data.aliasTooLong(maxAlias) };
    if (!aliases.some(kept => foldText(kept) === foldText(line))) aliases.push(line);
  }
  if (aliases.length > maxAliases) return { error: t.data.tooManyAliases(maxAliases) };
  return { patch: JSON.stringify(aliases) === JSON.stringify(data.aliases ?? []) ? {} : { aliases: aliases.length ? aliases : null } };
}

/** The contact the directory already has with that address, from a refused creation (409 `duplicate_contact`). */
export function duplicateContactId(failure: unknown): string | null {
  return failure instanceof ApiError && failure.code === "duplicate_contact" && typeof failure.problem?.contactId === "string" ? failure.problem.contactId : null;
}

/** A phone as the server's identity reads it (EntityIdentity.Phone, mirrored here incl. the +420 default): E.164, +420 for nine digits; null for a format it does not know. */
export function phoneKey(phone: string): string | null {
  let compact = phone.replace(/[\s().\-/]/g, "");
  if (compact.startsWith("00")) compact = `+${compact.slice(2)}`;
  const plus = compact.startsWith("+"), digits = plus ? compact.slice(1) : compact;
  if (!/^\d+$/.test(digits)) return null;
  if (plus && digits.length >= 8 && digits.length <= 15 && digits[0] !== "0") return `+${digits}`;
  return !plus && digits.length === 9 && digits[0] !== "0" ? `+420${digits}` : null;
}

/** The contact a merge leaves (spec 2026-10-30 §5, MergeContactsHandler.Merged): `target` with the fields it lacks taken from `source`,
 * and the source's e-mails (by lower case) and phones (by E.164) the target does not have yet. */
export function mergedContact(target: ContactData, source: ContactData): ContactData {
  const merged = { ...target } as Record<string, unknown>;
  for (const key of fields) {
    const mine = target[key], theirs = source[key];
    if (Array.isArray(theirs)) {
      const have = (mine as string[] | undefined) ?? [];
      const same = key === "phones" ? (a: string, b: string) => a === b || (phoneKey(a) !== null && phoneKey(a) === phoneKey(b)) : (a: string, b: string) => a.toLowerCase() === b.toLowerCase();
      merged[key] = [...have, ...theirs.filter((value, index) => !have.some(kept => same(kept, value)) && theirs.findIndex(other => same(other, value)) === index)];
    } else if (!filled(mine) && filled(theirs)) merged[key] = theirs;
  }
  return merged as unknown as ContactData;
}

// The labels are read when a merge is checked (a function), not when the module loads.
const listLabels = () => ({ emails: t.data.emails, phones: t.data.phones, aliases: t.data.aliases });
/** Why the server refuses this merge (MergeContactsHandler.Merged, the same words): a list of the merged contact over `maxAliases`
 * entries; null when it fits. */
export function mergeOverCapacity(target: ContactData, source: ContactData): string | null {
  const merged = mergedContact(target, source);
  for (const [key, label] of Object.entries(listLabels()) as [keyof ReturnType<typeof listLabels>, string][]) {
    const have = target[key]?.length ?? 0, all = merged[key]?.length ?? 0;
    if (all > maxAliases) return t.data.overCapacity(label, all, have, maxAliases);
  }
  return null;
}
