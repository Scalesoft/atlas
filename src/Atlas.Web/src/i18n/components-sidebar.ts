import { translate } from ".";

/** The sidebar's space (SidebarSpace), small because the shell shows it at once; the directory, draft approval and space contents are in components-spaces. The Czech source. */
const cs = {
  sidebar: {
    openSpace: "Otevřený prostor",
    pinSpace: (name: string) => `Připnout prostor ${name}`,
    pin: "Připnout",
    unpin: "Odepnout",
    collapseSpace: (name: string) => `Sbalit prostor ${name}`,
    expandSpace: (name: string) => `Rozbalit prostor ${name}`,
    collapseTitle: "Sbalit prostor",
    expandTitle: "Rozbalit prostor",
    sortedTip: "Stránky řazené abecedně",
    sortedSr: ", stránky řazené abecedně",
    pinnedByAdminTip: "Připnuto správcem",
    pinnedByAdminSr: ", připnuto správcem",
    findIn: (name: string) => `Najít ve stromu prostoru ${name}`,
    moreActions: (name: string) => `Další akce prostoru ${name}`,
    find: "Najít ve stromu",
    expandAll: "Rozbalit vše",
    collapseAll: "Sbalit vše",
    sortAlpha: "Řadit abecedně",
    dragPages: "Přesouvat stránky tažením",
    searchWiki: (query: string) => `Hledat „${query}“ v celé wiki`,
    matches: (count: number, title: string) => `Shod: ${count}, Enter otevře ${title}`,
    noMatch: "Nic neodpovídá, Enter hledá v celé wiki",
    pagesOf: (name: string) => `Stránky prostoru ${name}`,
    empty: "Prostor zatím nemá žádné stránky.",
    loadFailed: "Stránky se nepodařilo načíst.",
    retry: "Zkusit znovu",
    loading: "Načítání stránek…",
  },
};
export type SidebarMessages = typeof cs;

export default translate(cs, { en: () => import("./components-sidebar.en") });
