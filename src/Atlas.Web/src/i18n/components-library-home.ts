import { plural, translate } from ".";

/** The library page and the space header (library/LibraryPage, LibraryContent, SpaceHeader), small because the shell shows them at once; covers, gallery, about and pins are in components-library. The Czech source. */
const cs = {
  page: {
    title: "Knihovna",
    intro: "Prostory, ke kterým máte přístup, a návrat k vaší poslední práci.",
    create: "Nový prostor",
    tabs: "Části knihovny",
    parts: {
      spaces: { title: "Prostory", short: "" },
      personal: { title: "Můj prostor", short: "Moje" },
      shared: { title: "Sdíleno se mnou", short: "Sdíleno" },
      recent: { title: "Nedávné", short: "" },
      favorites: { title: "Oblíbené", short: "" },
      drafts: { title: "Moje koncepty", short: "Koncepty" },
      updates: { title: "Poslední publikace", short: "Publikace" },
      contacts: { title: "Kontakty", short: "" },
      notifications: { title: "Oznámení", short: "" },
    },
    unread: (count: number) => `nepřečtených ${count}`,
  },
  content: {
    loadingSpaces: "Načítání prostorů…",
    noneOpenedTitle: "Zatím jste nic neotevřeli",
    noneOpenedText: "Otevřete stránku v prostoru vlevo, použijte hledání, nebo si vyberte z prostorů, ke kterým máte přístup.",
    browse: "Procházet prostory",
  },
  /** Counts with their words: „3 stránky“, „1 koncept“. */
  pages: (count: number) => plural(count, { one: "# stránka", few: "# stránky", other: "# stránek" }),
  drafts: (count: number) => plural(count, { one: "# koncept", few: "# koncepty", other: "# konceptů" }),
  access: "Váš přístup",
  accessOf: (role: string) => `Váš přístup: ${role}`,
  header: {
    lastChange: "Poslední změna",
    settings: "Nastavení prostoru",
    newContent: "Nový obsah v prostoru",
    newTitle: "Nová stránka nebo tabule",
    new: "Nový",
    page: "Stránka",
    board: "Tabule",
    newPage: "Nová stránka",
  },
};
export type LibraryHomeMessages = typeof cs;

export default translate(cs, { en: () => import("./components-library-home.en") });
