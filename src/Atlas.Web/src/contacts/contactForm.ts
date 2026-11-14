import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";
import t from "../i18n/contacts";
import { cleanText, contactProps } from "../boards/boardTargets";

/**
 * The form of a contact: the fields of the editor's contact component that the directory stores (the directory, a contact card and
 * the board's picker use it), validated by the editor's own `normalizeContact` (name required, at most 20 phones and 20 e-mails, the phone and e-mail patterns, an http(s)
 * website, no control characters), so a contact made here passes wherever the editor's does. `normalize` is passed in: the picker
 * imports the editor plugin only when the form is used, a test passes the real one.
 * What the server refuses on top of that (a tab, other control characters, bidirectional controls) is cleaned
 * first, and the contact is what `contactProps` keeps of the result, so it is exactly what is validated and what is stored.
 */
export interface ContactFormValues {
  name: string; title: string; organization: string; department: string;
  /** One address per line; a comma or semicolon separates too. */
  emails: string;
  /** One number per line. */
  phones: string;
  website: string; address: string;
}
export type ContactFormErrors = Partial<Record<keyof ContactFormValues, string>>;

export const emptyContactForm = (): ContactFormValues => ({ name: "", title: "", organization: "", department: "", emails: "", phones: "", website: "", address: "" });

/** The editor words these three itself; for the others it names the field by its key, which is no Czech. */
const labels = () => ({ title: t.form.title, organization: t.form.organization, department: t.form.department, address: t.form.address });

const line = (text: string) => cleanText(text.replace(/\s+/g, " ")).trim();
const list = (text: string) => text.split(/[\r\n,;]+/).map(line).filter(Boolean);
/** "example.test" is a website people mean as https; the editor would keep it as a relative address and the card could not link it. */
const web = (text: string) => { const value = line(text); return value && !/^[a-z][a-z0-9+.-]*:/i.test(value) ? `https://${value}` : value; };

export function validateContactForm(values: ContactFormValues, normalize: (value: unknown) => unknown): { contact: MarkdownContact | null; errors: ContactFormErrors } {
  const address = values.address.split(/\r\n|[\r\n]/).map(line).join("\n").trim();
  const fields = {
    name: line(values.name), title: line(values.title), organization: line(values.organization), department: line(values.department),
    emails: list(values.emails), phones: list(values.phones), website: web(values.website), address,
  };
  const filled = Object.fromEntries(Object.entries(fields).filter(([, value]) => (Array.isArray(value) ? value.length : value)));
  const errors: ContactFormErrors = {};
  // Each field on its own, so every wrong one is named at once (the editor stops at the first).
  for (const key of Object.keys(fields) as (keyof ContactFormValues)[]) {
    try {
      normalize(key === "name" ? { name: fields.name } : { name: "x", [key]: filled[key] });
    } catch (failure) {
      errors[key] = key in labels() ? t.form.invalid(labels()[key as keyof ReturnType<typeof labels>]) : failure instanceof Error ? failure.message : t.form.invalidField;
    }
  }
  if (Object.keys(errors).length) return { contact: null, errors };
  return { contact: contactProps(normalize(filled)), errors };
}
