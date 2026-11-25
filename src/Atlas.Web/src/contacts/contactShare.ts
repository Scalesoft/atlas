import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";
import { plainTitle } from "../objects/relationsApi";
import type { ContactData } from "./contactData";

/** The card as a phone shows it: every field of a directory contact is inline Markdown, a vCard and its QR code carry the plain text. */
export function plainContact({ name, title, organization, department, emails, phones, website, address }: ContactData): MarkdownContact {
  const plain = (value?: string) => plainTitle(value) || undefined;
  return { name: plainTitle(name), title: plain(title), organization: plain(organization), department: plain(department), emails, phones, website,
    address: address?.split("\n").map(line => plainTitle(line)).filter(Boolean).join("\n") || undefined };
}

/** The contact as the .vcf a phone saves (the editor's own business card). */
export async function downloadCard(contact: ContactData) {
  const { downloadContacts } = await import("./pageContacts");
  downloadContacts([plainContact(contact)]);
}
