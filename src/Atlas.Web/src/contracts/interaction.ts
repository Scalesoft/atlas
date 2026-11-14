/*
 * The interaction contract (docs/interaction-rules.md, P16 „Konzistence vynucená“): the rules in short, the checks the contract
 * suites run on every interactive element, and the deviations they still accept. One shape for every surface:
 *   - editor (area A): src/Atlas.Markdown/src/contracts/interaction.contract.test.ts, deviations in ../../../Atlas.Markdown/src/contracts/interaction-deviations.json
 *   - editor in Atlas (area A, Atlas-only blocks such as the requirement): src/contracts/editorHostInteraction.contract.test.ts
 *   - boards (area B): src/contracts/boardInteraction.contract.test.ts
 *   - app pickers and lists (area C): src/contracts/appInteraction.contract.test.ts
 *   - AI (area D, P11): src/contracts/aiInteraction.contract.test.ts
 * Each deviation is a TODO of an alignment area: delete the row when the element follows the rule (the suite then fails until it
 * is deleted, so the list never lies). The gallery („Pravidla ovládání“) shows the rules and these lists live.
 */
import editorDeviations from "../../../Atlas.Markdown/src/contracts/interaction-deviations.json";
import editorHostDeviations from "./deviations/editorHost.json";
import boardDeviations from "./deviations/board.json";
import appDeviations from "./deviations/app.json";
import aiDeviations from "./deviations/ai.json";

export type InteractionArea = "A" | "B" | "C" | "D";
export const interactionAreas: Record<InteractionArea, string> = { A: "Editor stránky", B: "Tabule", C: "Aplikace", D: "AI" };

/** One accepted break of a rule: the element (a descriptor id), the check, why, and where the inventory names it. */
export interface InteractionDeviation {
  readonly element: string;
  readonly check: string;
  readonly reason: string;
  /** The inventory row, e.g. „A#1“ = page editor inconsistency 1. */
  readonly ref?: string;
  readonly area?: InteractionArea;
}

/** The rules in short (the gallery's summary); the full text is docs/interaction-rules.md. */
export const interactionRules = [
  { id: "P1", title: "Úprava začíná na prvku", text: "Klik (na tabuli Enter, F2, dvojklik) začne úpravu na místě, druhý klik ji zavře; Enter nebo F2 na vybraném prvku totéž." },
  { id: "P2", title: "Jedna nabídka akcí", text: "Ctrl+mezerník (Alt+Enter, Ctrl+., Shift+F10, pravé tlačítko) všude stejná nabídka; první řádek předvybraný." },
  { id: "P3", title: "Esc", text: "Zavře poslední otevřenou věc. Koncept zahodí, průběžně uložené jen zavře (vrací Ctrl+Z). Ve vyhledávání první Esc smaže text." },
  { id: "P4", title: "Odchod z pole a klik mimo", text: "Tab nebo klik jinam platnou hodnotu uloží; neplatná nechá pole otevřené s chybou. Klik mimo panel s „Použít“ použije." },
  { id: "P5", title: "Ukládání", text: "Průběžně bez tlačítka; „Použít“ (Ctrl+Enter) jen u velkých změn; „Uložit“ jen na stránkách nastavení." },
  { id: "P6", title: "Zpět", text: "Ctrl+Z / Ctrl+Y odkudkoli, jedna akce = jeden krok; smazání, přesun, přejmenování a převod ukáže oznámení se „Zpět“." },
  { id: "P7", title: "Fokus", text: "Po úpravě na upraveném prvku, po zavření panelu tam, odkud byl otevřen; nikdy na <body>, žádné časovače." },
  { id: "P8", title: "Výběry a vyhledávání", text: "První výsledek předvybraný, Enter ho vezme, Tab vezme návrh, šipky se pohybují; stejná jména rozlišená." },
  { id: "P9", title: "Čip v textu", text: "Jedna komponenta a vzhled; Ctrl+Shift+mezerník převede na text; jednoznačné se převede samo, víc možností = nerozhodnutý čip." },
  { id: "P10", title: "Čtení", text: "Co nemění obsah, funguje i při čtení; úpravy při čtení nejsou vidět." },
  { id: "P11", title: "AI", text: "Stejné záměry nad čímkoli, na co jde ukázat; výsledek je návrh k potvrzení; když AI neumí odpovědět, není vidět." },
  { id: "P12", title: "Vzhled a slova", text: "Jedny tokeny stavů (najetí, vybráno, upravuje se, fokus, chyba, nerozhodnuto) a motion tokeny; slova Upravit, Použít, Zrušit, Odebrat, Smazat, Zpět." },
  { id: "P13", title: "Načítání", text: "Hned zobrazit, co je; načítané místo vyhrazené kostrou ve správném rozměru." },
  { id: "P14", title: "Stránka a tabule", text: "Stejný prvek se na tabuli chová jako ve stránce; tabule přidává jen výběr a přesun." },
  { id: "P15", title: "Provázání samo od sebe", text: "Entity se poznají při psaní; nejednoznačné čeká na výběr; kontext řadí návrhy; mezery jsou vidět." },
  { id: "P16", title: "Konzistence vynucená", text: "Jen sdílené díly; kontraktní test v CI projde každý prvek; změněná ukázka v galerii dostane řádek „Ovládání“." },
  { id: "P17", title: "Svižnost a responzivita", text: "První odezva do 100 ms bez čekání na síť; psaní nikdy nezaostává; nabídky hned z mezipaměti; 375 px a dotyk; těžké moduly líně." },
] as const;

/** The checks the contract suites run; a deviation names one of them. */
export const interactionChecks = {
  "P1.start": { rule: "P1", text: "Klik na obsah (na tabuli Enter/F2/dvojklik) začne úpravu." },
  "P1.enter": { rule: "P1", text: "Enter nebo F2 na vybraném prvku začne tutéž úpravu." },
  "P1.toggle": { rule: "P1", text: "Druhý klik úpravu zavře." },
  "P1.board-select": { rule: "P1", text: "Na tabuli první klik jen vybere (D1)." },
  "P2.menu": { rule: "P2", text: "Ctrl+mezerník na vybraném prvku otevře nabídku akcí s předvybraným řádkem." },
  "P3.esc": { rule: "P3", text: "Esc úpravu zavře: koncept vrátí, průběžně uložené nechá." },
  "P3.esc-second": { rule: "P3", text: "Další Esc zruší výběr; klávesnice nezůstane na <body>." },
  "P3.search-esc": { rule: "P3", text: "Ve vyhledávání první Esc smaže text, druhý zavře." },
  "P4.outside": { rule: "P4", text: "Klik mimo platnou hodnotu uloží." },
  "P4.blur": { rule: "P4", text: "Odchod fokusu (Tab) platnou hodnotu uloží." },
  "P6.undo": { rule: "P6", text: "Po uložení Ctrl+Z tam, kde je klávesnice, vrátí změnu jedním krokem." },
  "P6.toast": { rule: "P6", text: "Smazání, přesun, přejmenování ukáže oznámení se „Zpět“." },
  "P7.focus": { rule: "P7", text: "Po dokončení je klávesnice na upraveném prvku, nikdy na <body>." },
  "P7.return": { rule: "P7", text: "Po zavření panelu nebo výběru se klávesnice vrátí tam, odkud byl otevřen." },
  "P8.first": { rule: "P8", text: "První výsledek je předvybraný." },
  "P8.enter": { rule: "P8", text: "Psaní + Enter vezme první výsledek; prázdné pole nic nevezme." },
  "P8.arrows": { rule: "P8", text: "Šipky se pohybují po výsledcích." },
  "P8.tab": { rule: "P8", text: "Tab vezme návrh." },
  "P11.offered": { rule: "P11", text: "Když AI umí odpovědět, nabídka ukáže záměry nad tím, na co se ukazuje; při čtení jen ty, které nic nemění." },
  "P11.hidden": { rule: "P11", text: "Když AI odpovědět neumí, žádná položka AI není vidět (žádná hláška) a ostatní akce zůstanou." },
  "P11.proposal": { rule: "P11", text: "Záměr, který mění obsah, je návrh k potvrzení (Použít / Zahodit); Použít je jeden krok zpět." },
  "P12.selected": { rule: "P12", text: "Vybraný prvek nese sdílenou třídu stavu „vybráno“." },
  "P13.skeleton": { rule: "P13", text: "Načítání má kostru ve správném rozměru, ne holý text." },
  "P17.sync": { rule: "P17", text: "Úprava (pole, nabídka, panel) se ukáže ve stejné úloze jako gesto, bez čekání." },
  "P17.keystroke": { rule: "P17", text: "Úhoz v dlouhém dokumentu (2 000 bloků) se vejde do rozpočtu i s rozpoznáním jmen; hledání jmen, které psaní odloží, běží po krátkých úlohách a podklad pro ukládání se připraví do 100 ms." },
  "P17.cached": { rule: "P17", text: "Výběr ukáže první řádky hned, bez prázdného bliknutí." },
} as const;
export type InteractionCheck = keyof typeof interactionChecks;

/** The shared parts the rules require (P16), as the gallery links them. */
export const interactionSharedParts = [
  { name: "Oznámení se „Zpět“", where: "composables/useToast.ts", text: "useToast().success(text, { undo }) — jediná cesta (P6); App.vue notify() zanikl, v záhlaví stránky zůstává jen krátký stav revize (confirmDocument), o jeho podobě rozhodne majitel." },
  { name: "Návrat fokusu", where: "lib/focusReturn.ts", text: "rememberFocus() před otevřením panelu, vrácená funkce po zavření (P7)." },
  { name: "Tokeny stavů", where: "@atlas/markdown/styles/states.css", text: "--state-hover-bg, --state-selected, --state-editing, --state-focus-ring, --state-error, --state-unresolved (P12)." },
  { name: "Čip v textu", where: "@atlas/markdown features/chips/chip.ts", text: "createMarkdownChip / markdownChipSpec (P9)." },
  { name: "Nabídka akcí", where: "@atlas/markdown features/intentions", text: "Ctrl+mezerník, registerMarkdownIntentions; tlačítka lišty vybraného prvku jsou v ní samy jako řádky, tabule a graf vazeb ji otevírají přes openMarkdownIntentionsMenu (P2)." },
  { name: "Výběr se seznamem", where: "lib/pickEnter.ts", text: "isPickEnter / pickOnEnter / focusFirst (P8)." },
  { name: "Klávesy hledání a výběru", where: "lib/searchKeys.ts", text: "useSearchKeys (Esc smaže napsané, Enter a Tab vezmou první výsledek, Enter čeká na odpověď, šipky move() / lit), listKeys, ownEscape, clearOnEscape (P3, P8)." },
  { name: "Kostra načítání", where: "LoadingSkeleton / LoadingNote / LoadingRegion", text: "P13." },
] as const;

/** Every surface's accepted deviations, for the gallery. */
export const interactionDeviations: Readonly<Record<InteractionArea, readonly InteractionDeviation[]>> = (() => {
  const all: InteractionDeviation[] = [
    ...(editorDeviations as InteractionDeviation[]).map(row => ({ area: "A" as const, ...row })),
    ...(editorHostDeviations as InteractionDeviation[]).map(row => ({ area: "A" as const, ...row })),
    ...(boardDeviations as InteractionDeviation[]).map(row => ({ area: "B" as const, ...row })),
    ...(appDeviations as InteractionDeviation[]).map(row => ({ area: "C" as const, ...row })),
    ...(aiDeviations as InteractionDeviation[]).map(row => ({ area: "D" as const, ...row })),
  ];
  return { A: all.filter(row => row.area === "A"), B: all.filter(row => row.area === "B"), C: all.filter(row => row.area === "C"), D: all.filter(row => row.area === "D") };
})();
