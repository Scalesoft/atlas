import { formatNumber, translate } from ".";

const cs = {
  cancel: "Zrušit",
  saveBar: {
    save: "Uložit změny",
    message: "Máte neuložené změny",
    region: "Uložení změn",
    saved: "Uloženo",
    saving: "Ukládání…",
  },
  retry: "Zkusit znovu",
  dismissBanner: "Skrýt oznámení",
  dismissNote: "Skrýt upozornění",
  liveUpdate: { unavailable: "Živá aktualizace nedostupná.", refresh: "Obnovit" },
  tags: {
    empty: "Nic neodpovídá hledání.",
    remove: (label: string, twin?: string) => `Odebrat ${label}${twin ? ` (${twin})` : ""}`,
    search: (label: string) => `${label}: hledat`,
    suggestions: (label: string) => `${label}: návrhy`,
    more: (more: number, typing: boolean) => `+${formatNumber(more)} ${typing ? "dalších – upřesněte hledání" : "dalších – hledejte"}`,
  },
  choiceList: { empty: "Nic neodpovídá hledání. Zkuste jiné slovo.", search: (label: string) => `${label}: hledat` },
  shell: { searchSections: "Hledat sekci", noMatch: "Nic takového tu není.", sections: "Sekce", closeSections: "Zavřít sekce" },
  advanced: "Pokročilé",
  summary: "Souhrn",
  lazy: {
    title: "Nastavení se nepodařilo načíst",
    text: "Soubory aplikace se nepodařilo stáhnout. Zkontrolujte připojení, případně obnovte stránku: mohla vyjít nová verze.",
    retry: "Obnovit stránku",
  },
  trash: {
    purgeTitle: "Smazat trvale?",
    emptyTitle: "Koš je prázdný",
    failed: "Koš se nepodařilo načíst",
    reload: "Načíst znovu",
    loading: "Načítání koše…",
    restore: "Obnovit",
    restoreItem: (title: string) => `Obnovit: ${title}`,
    purge: "Smazat trvale",
    purgeItem: (title: string) => `Smazat trvale: ${title}`,
    actions: (title: string) => `Akce: ${title}`,
    purgeBody: "se smaže i s historií. Vrátit to nepůjde.",
    backupNote: "Kopie v dřívějších zálohách (archivy ZIP, historie zálohy Git) zůstanou; odstranit je z nich může jen správce záloh.",
    typeName: "Pro potvrzení opište název",
  },
};
export type SettingsShellMessages = typeof cs;

export default translate(cs, { en: () => import("./settings-shell.en") });
