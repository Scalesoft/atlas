import { translate } from ".";

/** Contacts (contacts/): validation, linking, sources, overview levels. Small: the editor imports it. The Czech source. */
const cs = {
  data: {
    invalidField: "Neplatné pole kontaktu.",
    aliasTooLong: (max: number) => `Zkratka nebo přezdívka může mít nejvýš ${max} znaků.`,
    tooManyAliases: (max: number) => `Kontakt může mít nejvýš ${max} zkratek a přezdívek.`,
    emails: "E-mail",
    phones: "Telefon",
    aliases: "Zkratky a přezdívky",
    overCapacity: (label: string, all: number, have: number, max: number) => `Kontakty nejde sloučit: pole „${label}“ by mělo ${all} hodnot (cílový kontakt ${have}, ze zdrojového ${all - have} dalších), kontakt jich unese nejvýše ${max}. Nejdřív u jednoho z nich některé odeberte.`,
  },
  form: {
    title: "Funkce",
    organization: "Organizace",
    department: "Oddělení",
    address: "Adresa",
    invalid: (label: string) => `Neplatné pole: ${label}.`,
    invalidField: "Neplatné pole.",
  },
  linking: {
    repeated: "Kontakt už tu je – vložen podruhé.",
    contactNamed: (name: string) => `Kontakt ${name}`,
    deleteFailed: (name: string, error: string) => `Kontakt ${name} se nepodařilo smazat: ${error}`,
    restoreFailed: (error: string) => `Kontakt se nepodařilo obnovit z koše: ${error}`,
  },
  slash: {
    inText: "Kontakt v textu",
    contact: "Kontakt",
  },
  photo: {
    resizeFailed: "Obrázek se nepodařilo zmenšit.",
  },
  sources: {
    page: "Stránka",
    pageOf: (name: string) => `Stránka ${name}`,
    pagePrefix: "Stránka ",
    account: "Účet Atlasu",
    directory: "Adresář",
  },
  levels: {
    names: "Jména",
    cards: "Karty",
    full: "Plné karty",
  },
  overview: {
    label: "Kontakty",
  },
};
export type ContactsMessages = typeof cs;

export default translate(cs, { en: () => import("./contacts.en") });
