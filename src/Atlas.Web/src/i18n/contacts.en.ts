import type { ContactsMessages } from "./contacts";

const en: ContactsMessages = {
  data: {
    invalidField: "Invalid contact field.",
    aliasTooLong: (max: number) => `An abbreviation or nickname can have at most ${max} characters.`,
    tooManyAliases: (max: number) => `A contact can have at most ${max} abbreviations and nicknames.`,
    emails: "E-mail",
    phones: "Phone",
    aliases: "Abbreviations and nicknames",
    overCapacity: (label: string, all: number, have: number, max: number) => `The contacts can't be merged: the field “${label}” would have ${all} values (target contact ${have}, ${all - have} more from the source), and a contact can hold at most ${max}. Remove some from one of them first.`,
  },
  form: {
    title: "Role",
    organization: "Organization",
    department: "Department",
    address: "Address",
    invalid: (label: string) => `Invalid field: ${label}.`,
    invalidField: "Invalid field.",
  },
  linking: {
    repeated: "The contact is already here – inserted a second time.",
    contactNamed: (name: string) => `Contact ${name}`,
    deleteFailed: (name: string, error: string) => `Couldn't delete the contact ${name}: ${error}`,
    restoreFailed: (error: string) => `Couldn't restore the contact from the trash: ${error}`,
  },
  slash: {
    inText: "Contact in text",
    contact: "Contact",
  },
  photo: {
    resizeFailed: "Couldn't resize the image.",
  },
  sources: {
    page: "Page",
    pageOf: (name: string) => `Page ${name}`,
    pagePrefix: "Page ",
    account: "Atlas account",
    directory: "Directory",
  },
  levels: {
    names: "Names",
    cards: "Cards",
    full: "Full cards",
  },
  overview: {
    label: "Contacts",
  },
};

export default en;
