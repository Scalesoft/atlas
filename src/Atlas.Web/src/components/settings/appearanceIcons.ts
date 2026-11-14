/**
 * The icon catalog of spaces and page icons: every meaningful drawing of the icon registry
 * (`@atlas/markdown/icons`) with its id, group and search words, and the translated names
 * (i18n/appearance-icons). Only the icon picker and the places that list or search icons import this
 * module; what the first screen needs (drawing a chosen icon by id, the colours) stays in appearance.ts.
 * The server accepts exactly these values (Platform/Appearance/AppearanceCatalog.cs).
 * Editor-only glyphs (text formatting, table rows and columns, handles) are left out.
 */
import { foldText } from "../../lib/foldText";
import { locale } from "../../i18n";
import names from "../../i18n/appearance-icons";

export interface AppearanceIcon {
  id: string;
  label: string;
  /** One of appearanceIconGroups for the 87; the Czech category of an icon of the icon pack (iconPack.ts). */
  group: string;
  /** Extra Czech and English words the search matches. */
  words?: string;
}
export type AppearanceIconGroup = "Obecné" | "Dokumenty" | "Lidé a týmy" | "Vývoj a provoz" | "Data a čísla" | "Bezpečnost" | "Stav a upozornění" | "Čas a místo";

export const appearanceIconGroups: readonly AppearanceIconGroup[] = ["Obecné", "Dokumenty", "Lidé a týmy", "Vývoj a provoz", "Data a čísla", "Bezpečnost", "Stav a upozornění", "Čas a místo"];

/** A catalog entry with its name read where it is shown (src/i18n/appearance-icons.ts), by the id. */
const named = (icon: Omit<AppearanceIcon, "label">): AppearanceIcon => ({ ...icon, get label() { return names.icons[icon.id] ?? icon.id; } });
/** The display name of a group (the Czech `group` stays the key). */
export const appearanceGroupLabel = (group: string) => names.groups[group] ?? group;
/** The display name of an icon pack category: by its id, the catalog's Czech label otherwise. */
export const appearanceCategoryLabel = (category: { id: string; label: string }) => names.categories[category.id] ?? category.label;

export const appearanceIcons: readonly AppearanceIcon[] = ([
  { id: "book-open", group: "Obecné", words: "příručka manuál dokumentace wiki book" },
  { id: "home", group: "Obecné", words: "úvod start home" },
  { id: "star", group: "Obecné", words: "oblíbené důležité favorite" },
  { id: "lightbulb", group: "Obecné", words: "tip inovace idea" },
  { id: "smile", group: "Obecné", words: "kultura zábava tým happy" },
  { id: "compass", group: "Obecné", words: "rozcestník navigace orientace" },
  { id: "megaphone", group: "Obecné", words: "komunikace marketing novinky announce" },
  { id: "ticket", group: "Obecné", words: "tiket podpora helpdesk support" },
  { id: "palette", group: "Obecné", words: "barvy grafika ui ux" },
  { id: "pin", group: "Obecné", words: "špendlík důležité" },
  { id: "box", group: "Obecné", words: "balík projekt modul package" },
  { id: "blocks", group: "Obecné", words: "produkt komponenty stavebnice" },
  { id: "plug", group: "Obecné", words: "připojení plugin rozšíření" },
  { id: "highlighter", group: "Obecné", words: "fix poznámky marker" },
  { id: "pencil", group: "Obecné", words: "psaní úpravy koncept" },
  { id: "search", group: "Obecné", words: "lupa vyhledávání research" },
  { id: "file-text", group: "Dokumenty", words: "stránka text soubor" },
  { id: "file-plus", group: "Dokumenty", words: "šablona vytvořit" },
  { id: "folder", group: "Dokumenty", words: "adresář archiv" },
  { id: "folder-plus", group: "Dokumenty", words: "adresář přidat" },
  { id: "folder-input", group: "Dokumenty", words: "import složka vstup" },
  { id: "layout-template", group: "Dokumenty", words: "rozvržení layout" },
  { id: "list", group: "Dokumenty", words: "výčet položky" },
  { id: "list-checks", group: "Dokumenty", words: "checklist kontrolní seznam todo" },
  { id: "list-ordered", group: "Dokumenty", words: "kroky pořadí návod" },
  { id: "scroll-text", group: "Dokumenty", words: "log audit protokol zápis" },
  { id: "quote", group: "Dokumenty", words: "výrok uvozovky" },
  { id: "clipboard-paste", group: "Dokumenty", words: "vložit kopie clipboard" },
  { id: "paperclip", group: "Dokumenty", words: "sponka soubor attachment" },
  { id: "image", group: "Dokumenty", words: "foto galerie grafika" },
  { id: "table", group: "Dokumenty", words: "přehled data grid" },
  { id: "type", group: "Dokumenty", words: "písmo typografie font" },
  { id: "hash", group: "Dokumenty", words: "tag kanál hashtag" },
  { id: "link", group: "Dokumenty", words: "url rozcestník link" },
  { id: "external-link", group: "Dokumenty", words: "web mimo" },
  { id: "users", group: "Lidé a týmy", words: "lidé skupina oddělení team" },
  { id: "workflow", group: "Lidé a týmy", words: "tým lidé skupina" },
  { id: "user", group: "Lidé a týmy", words: "osoba profil účet" },
  { id: "user-check", group: "Lidé a týmy", words: "ověřený onboarding přístup" },
  { id: "code-xml", group: "Vývoj a provoz", words: "kód programování developer" },
  { id: "square-code", group: "Vývoj a provoz", words: "konzole skript shell" },
  { id: "code", group: "Vývoj a provoz", words: "snippet programování" },
  { id: "database", group: "Vývoj a provoz", words: "sql data úložiště" },
  { id: "server", group: "Vývoj a provoz", words: "infrastruktura hardware hosting" },
  { id: "network", group: "Vývoj a provoz", words: "infrastruktura propojení lan" },
  { id: "monitor", group: "Vývoj a provoz", words: "obrazovka počítač desktop" },
  { id: "settings-2", group: "Vývoj a provoz", words: "konfigurace volby" },
  { id: "sliders-horizontal", group: "Vývoj a provoz", words: "parametry úpravy tuning" },
  { id: "filter", group: "Vývoj a provoz", words: "trychtýř výběr" },
  { id: "activity", group: "Vývoj a provoz", words: "aktivita puls metriky" },
  { id: "layout-dashboard", group: "Vývoj a provoz", words: "dashboard nástěnka" },
  { id: "refresh-cw", group: "Vývoj a provoz", words: "synchronizace aktualizace ci" },
  { id: "download", group: "Vývoj a provoz", words: "release export instalace" },
  { id: "upload", group: "Vývoj a provoz", words: "import nasazení deploy" },
  { id: "rotate-cw", group: "Vývoj a provoz", words: "opakování iterace sprint" },
  { id: "bar-chart-3", group: "Data a čísla", words: "graf reporting sloupce" },
  { id: "line-chart", group: "Data a čísla", words: "graf vývoj v čase analytika" },
  { id: "pie-chart", group: "Data a čísla", words: "koláč rozpočet finance" },
  { id: "donut", group: "Data a čísla", words: "prstenec podíl" },
  { id: "sigma", group: "Data a čísla", words: "suma matematika vzorec" },
  { id: "shield", group: "Bezpečnost", words: "bezpečnost štít" },
  { id: "shield-check", group: "Bezpečnost", words: "ověřeno compliance security" },
  { id: "shield-alert", group: "Bezpečnost", words: "hrozba riziko" },
  { id: "lock", group: "Bezpečnost", words: "soukromé tajné trezor" },
  { id: "lock-open", group: "Bezpečnost", words: "veřejné přístup" },
  { id: "key-round", group: "Bezpečnost", words: "heslo přístup credentials" },
  { id: "fingerprint", group: "Bezpečnost", words: "otisk přihlášení sso" },
  { id: "eye", group: "Bezpečnost", words: "oko náhled čtení" },
  { id: "eye-off", group: "Bezpečnost", words: "neviditelné důvěrné" },
  { id: "ban", group: "Bezpečnost", words: "blokace omezení" },
  { id: "info", group: "Stav a upozornění", words: "nápověda poznámka" },
  { id: "circle-help", group: "Stav a upozornění", words: "faq nápověda podpora" },
  { id: "circle-check", group: "Stav a upozornění", words: "splněno v pořádku" },
  { id: "circle-x", group: "Stav a upozornění", words: "chyba neplatné" },
  { id: "check", group: "Stav a upozornění", words: "fajfka ok" },
  { id: "triangle-alert", group: "Stav a upozornění", words: "pozor riziko" },
  { id: "octagon-alert", group: "Stav a upozornění", words: "kritické havárie" },
  { id: "octagon-x", group: "Stav a upozornění", words: "zastaveno" },
  { id: "hourglass", group: "Stav a upozornění", words: "přesýpací hodiny fronta" },
  { id: "calendar", group: "Čas a místo", words: "plán termíny akce" },
  { id: "clock", group: "Čas a místo", words: "hodiny termín" },
  { id: "history", group: "Čas a místo", words: "archiv minulost verze" },
  { id: "sun", group: "Čas a místo", words: "den léto" },
  { id: "moon", group: "Čas a místo", words: "noc pohotovost" },
  { id: "globe", group: "Čas a místo", words: "web internet mezinárodní" },
  { id: "map-pin", group: "Čas a místo", words: "pobočka adresa lokalita" },
  { id: "building-2", group: "Čas a místo", words: "firma kancelář zákazník" },
] satisfies Omit<AppearanceIcon, "label">[]).map(named);

/** The common icons of board 11, in its order; ids of the catalog. */
export const quickAppearanceIconIds: readonly string[] = [
  "layout-dashboard", "book-open", "home", "star", "lightbulb", "compass", "megaphone", "box", "plug", "pencil", "file-text", "folder", "list", "list-checks",
  "users", "user", "code-xml", "database", "server", "monitor", "activity", "bar-chart-3", "shield", "lock", "key-round", "calendar", "globe", "building-2",
];


/**
 * Icons the server still accepts and pages that already use them still show, but no picker offers
 * any more. The sparkles drawing is the one AI icon now (AiGlyph, plan §0a.3 and O9); pages that
 * chose it as "Novinky" keep it, and their stored decoration does not change.
 */
export const retiredAppearanceIcons: readonly AppearanceIcon[] = [{ id: "sparkles", group: "Obecné" }].map(named);

interface FoldedIcon { label: string; labelWords: string[]; keywords: string[]; haystack: string }
const folded = new WeakMap<AppearanceIcon, FoldedIcon>();
const splitWords = (text: string) => text.split(/[^\p{L}\p{N}]+/u).filter(Boolean);
function foldIcon(icon: AppearanceIcon) {
  let entry = folded.get(icon);
  if (!entry) {
    const label = foldText(icon.label);
    entry = {
      label,
      labelWords: splitWords(label),
      keywords: splitWords(foldText(`${icon.words ?? ""} ${icon.id}`)),
      // Without the group: the category is a filter of its own, and "data" would otherwise find all of "Vývoj a data".
      haystack: foldText(`${icon.label} ${icon.words ?? ""} ${icon.id}`),
    };
    folded.set(icon, entry);
  }
  return entry;
}
/** Words shorter than this match only where a word starts: inside a word one letter finds nearly every icon. */
const anywhereMinLength = 3;
/** 1 the label starts with the word, 2 a word of the label does, 3 a keyword does, 4 it occurs anywhere in the label, words or id (from three characters), 0 not at all. */
function rankWord(icon: FoldedIcon, word: string) {
  if (icon.label.startsWith(word)) return 1;
  if (icon.labelWords.some((item) => item.startsWith(word))) return 2;
  if (icon.keywords.some((item) => item.startsWith(word))) return 3;
  return word.length >= anywhereMinLength && icon.haystack.includes(word) ? 4 : 0;
}
/** 0 when the whole label is the query, else the worst rank of its words; undefined when a word matches nothing. */
function rankIcon(icon: FoldedIcon, text: string, words: readonly string[]) {
  if (icon.label === text) return 0;
  let rank = 1;
  for (const word of words) {
    const wordRank = rankWord(icon, word);
    if (!wordRank) return undefined;
    rank = Math.max(rank, wordRank);
  }
  return rank;
}
const coreIconIds = new Set(appearanceIcons.map((icon) => icon.id));
const labelCollator = new Intl.Collator(locale);

/**
 * The icons every word of the query matches, best first; at the same rank the 87 come before icons of the icon pack,
 * then by label. An empty query returns `icons` itself.
 */
export function findAppearanceIcons<T extends AppearanceIcon>(query: string, icons: readonly T[]): readonly T[] {
  const text = foldText(query.trim()).replace(/\s+/g, " ");
  if (!text) return icons;
  const words = text.split(" ");
  return icons
    .flatMap((icon) => {
      const rank = rankIcon(foldIcon(icon), text, words);
      return rank === undefined ? [] : [{ icon, rank }];
    })
    .sort((a, b) =>
      a.rank - b.rank
      || Number(coreIconIds.has(b.icon.id)) - Number(coreIconIds.has(a.icon.id))
      || labelCollator.compare(a.icon.label, b.icon.label)
      || labelCollator.compare(a.icon.id, b.icon.id))
    .map(({ icon }) => icon);
}
export const appearanceIconLabel = (id: string) => [...appearanceIcons, ...retiredAppearanceIcons].find((icon) => icon.id === id)?.label ?? id;
