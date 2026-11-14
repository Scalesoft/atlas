import { formatNumber, plural, translate } from ".";

const cs = {
  picker: {
    icon: "Ikona",
    current: "Současná",
    none: "Bez ikony",
    preview: (icon: string, color: string) => `Náhled: ${icon}, ${color}`,
    spaceName: "Název prostoru",
    pageName: "Název stránky",
    fewer: "Méně ikon",
    more: (total: string) => `Další ikony (${total})`,
    failedTitle: "Další ikony se nepodařilo načíst",
    failedText: "Zkontrolujte připojení a zkuste to znovu. Běžné ikony výše fungují dál.",
    loading: "Načítání ikon…",
    color: "Barva",
  },
  colors: {
    label: "Barva",
    fewer: "Méně barev",
    more: (count: number) => `Další barvy a tóny (${count})`,
    note: (hues: number, tones: number) => `${hues} barev × ${tones} tónů. Každá kombinace má ověřený kontrast ikony.`,
    all: "Všechny barvy",
  },
  browser: {
    all: "Vše",
    recommended: "Doporučené",
    recent: "Naposledy použité",
    found: (matched: number, total: number) => `Nalezeno ${formatNumber(matched)} z ${formatNumber(total)}`,
    icons: (n: number) => plural(n, { one: "# ikona", few: "# ikony", other: "# ikon" }),
    search: "Hledat ikonu",
    searchPlaceholder: (total: number) => `Hledat z ${formatNumber(total)} ikon`,
    categories: "Kategorie ikon",
    listbox: "Ikony",
    showAll: (matched: number) => `Zobrazit všech ${formatNumber(matched)}`,
    empty: "Žádná ikona neodpovídá. Zkuste jiné slovo, třeba „lidé“, „data“ nebo „zámek“.",
    everywhere: "Hledat ve všech kategoriích",
  },
};
export type SettingsAppearanceMessages = typeof cs;

export default translate(cs, { en: () => import("./settings-appearance.en") });
