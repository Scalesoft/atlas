import { translate } from ".";

/** Small shared pieces in components/: SaveState, SearchSuggestions, SidebarResizer, SimilarPagesSuggestion, SpaceMenuItems,
 * TagSearchSuggestions, UnsavedChangesDialog, WorkspaceSidePanel. The Czech source. */
const cs = {
  saveState: {
    saving: "Ukládám…",
    saved: "Uloženo",
    offline: "Offline, uloží se po připojení",
    paused: "Offline: psaní je pozastavené",
    error: "Chyba",
  },
  suggestions: {
    recent: "Nedávno otevřené",
    favorites: "Oblíbené",
    region: "Nedávné a oblíbené stránky",
    loading: "Načítání nedávných a oblíbených stránek…",
    retry: "Zkusit znovu",
    minTitle: "Zadejte alespoň dva znaky",
    minText: "Hledáme v názvech i obsahu. Zde se objeví vaše nedávné a oblíbené stránky.",
    draft: "Koncept",
  },
  sidebarResizer: {
    label: "Šířka boční navigace",
    pixels: (value: number) => `${value} pixelů`,
    hint: "Tažením změnit šířku · šipky vlevo/vpravo · dvojklik obnoví výchozí šířku",
  },
  spaceMenu: {
    newPage: "Nová stránka",
    newBoard: "Nová tabule",
    settings: "Nastavení prostoru",
    templates: "Šablony stránek",
    access: "Přístupy",
    pinPolicy: "Připnutí pro všechny…",
    trash: "Koš stránek",
    exportEmpty: "Prostor zatím neobsahuje žádné stránky.",
    exportHint: "ZIP uložených verzí dostupných stránek včetně příloh. Nejde o zálohu serveru.",
    export: "Exportovat prostor",
    contentHealth: "Stav obsahu",
  },
  tagSearch: {
    tags: "Tagy",
    failed: "Tagy se nepodařilo načíst.",
    found: (count: number) => `Nalezené tagy: ${count}. Na tlačítka tagů přejdete klávesou Tab.`,
    region: "Nalezené tagy",
    pagesWith: (tag: string) => `Stránky s tagem ${tag}`,
    retry: "Zkusit znovu",
  },
  unsaved: {
    title: "Neuložené změny",
    saveOrDiscard: "Máte rozepsané změny. Chcete je uložit, nebo zahodit?",
    discard: "Máte rozepsané změny. Chcete je zahodit?",
    stay: "Zůstat",
    discardButton: "Zahodit",
    saving: "Ukládání…",
    save: "Uložit",
  },
  workspacePanel: {
    close: "Zavřít panel",
    tools: "Nástroje stránky",
    width: (label: string) => `Šířka panelu: ${label}`,
  },
};
export type ComponentsMessages = typeof cs;

export default translate(cs, { en: () => import("./components-c.en") });
