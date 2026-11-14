/**
 * The page editor's keys as data, for the host's shortcut overview (Atlas shows it on Ctrl+/ or ?) and the menus' hints.
 * No editor code: a shell may import it without loading the editor. Keep in step with extensions/KeyboardFirst.ts and the
 * tiptap defaults it lists.
 */
export interface MarkdownShortcut {
  /** Key names joined by „+“ in the overview; `Mod` is Ctrl (⌘ on Apple devices). Letters upper case. */
  readonly keys: readonly string[]
  /** A second step pressed after `keys` (Escape, then Tab). */
  readonly then?: readonly string[]
  /** Czech, the same words as the menus and tooltips. */
  readonly label: string
  readonly group: 'Text' | 'Bloky' | 'Vkládání' | 'Seznamy a tabulky' | 'Pohyb a výběr'
}
// `keys`: space-separated keys of one press; „, “ separates a second step („Escape, Tab“).
const shortcut = (group: MarkdownShortcut['group'], keys: string, label: string): MarkdownShortcut => {
  const [first, then] = keys.split(', ')
  return { keys: first!.split(' '), ...then ? { then: then.split(' ') } : {}, label, group }
}

/** Every key of the page editor, for the host's shortcut overview (Ctrl+/ or ?). Typed Markdown is in `markdownTypingShortcuts`. */
export const markdownKeyboardShortcuts: readonly MarkdownShortcut[] = Object.freeze([
  shortcut('Text', 'Mod B', 'Tučně'),
  shortcut('Text', 'Mod I', 'Kurzíva'),
  shortcut('Text', 'Mod U', 'Podtržené'),
  shortcut('Text', 'Mod Shift S', 'Přeškrtnuté'),
  shortcut('Text', 'Mod E', 'Inline kód'),
  shortcut('Text', 'Mod Shift H', 'Zvýraznit'),
  // No Ctrl+K (Atlas's search), Ctrl+S (save the draft), Ctrl+J (assistant) or Ctrl+Shift+P (commands): the host's everywhere.
  shortcut('Text', 'Mod Z', 'Zpět'),
  shortcut('Text', 'Mod Shift Z', 'Znovu'),
  shortcut('Text', 'Mod F', 'Najít a nahradit'),
  // Ctrl+Shift+digit reaches a Czech keyboard (its Ctrl+Alt is AltGr); tiptap's Ctrl+Alt+digit still works elsewhere.
  shortcut('Bloky', 'Mod Shift 0', 'Běžný text'),
  shortcut('Bloky', 'Mod Shift 1', 'Nadpis 1'),
  shortcut('Bloky', 'Mod Shift 2', 'Nadpis 2'),
  shortcut('Bloky', 'Mod Shift 3', 'Nadpis 3'),
  shortcut('Bloky', 'Mod Shift 4', 'Nadpis 4'),
  shortcut('Bloky', 'Mod Shift 5', 'Nadpis 5'),
  shortcut('Bloky', 'Mod Shift 6', 'Nadpis 6'),
  shortcut('Bloky', 'Mod Shift B', 'Citace'),
  shortcut('Bloky', 'Mod Alt C', 'Blok kódu'),
  // Alt+↑/↓ fold and unfold the section of the heading above the caret; moving is Ctrl+Shift+↑/↓, as in Notion.
  shortcut('Bloky', 'Mod Shift ArrowUp', 'Posunout blok nahoru'),
  shortcut('Bloky', 'Mod Shift ArrowDown', 'Posunout blok dolů'),
  shortcut('Bloky', 'Alt ArrowUp', 'Sbalit sekci nadpisu'),
  shortcut('Bloky', 'Alt ArrowDown', 'Rozbalit sekci nadpisu'),
  shortcut('Bloky', 'Alt Shift ArrowUp', 'Duplikovat blok nahoru'),
  shortcut('Bloky', 'Alt Shift ArrowDown', 'Duplikovat blok dolů'),
  shortcut('Bloky', 'Mod D', 'Duplikovat blok'),
  shortcut('Bloky', 'Mod Shift Backspace', 'Smazat blok'),
  shortcut('Bloky', 'Delete', 'Smazat vybraný blok (nadpis i se sekcí)'),
  shortcut('Bloky', 'Alt F10', 'Ovládání bloku; nad vybraným textem lišta formátování (i Odkaz)'),
  shortcut('Seznamy a tabulky', 'Mod Shift 8', 'Odrážkový seznam'),
  shortcut('Seznamy a tabulky', 'Mod Shift 7', 'Číslovaný seznam'),
  shortcut('Seznamy a tabulky', 'Mod Shift 9', 'Checklist'),
  // The digits are all taken (headings, lists, Běžný text): D as datum.
  shortcut('Seznamy a tabulky', 'Mod Shift D', 'Časová odrážka'),
  shortcut('Seznamy a tabulky', 'Tab', 'Zanořit položku, další buňka (na konci tabulky nový řádek)'),
  shortcut('Seznamy a tabulky', 'Shift Tab', 'Vynořit položku, předchozí buňka'),
  shortcut('Vkládání', '/', 'Vložit blok nebo komponentu (píšete dál: /tabulka 3x4, /kód ts, /kontakt jan)'),
  shortcut('Vkládání', '@', 'Zmínit osobu'),
  shortcut('Vkládání', '[[', 'Odkaz na stránku'),
  shortcut('Vkládání', 'Shift Enter', 'Nový řádek ve stejném odstavci'),
  shortcut('Vkládání', 'Mod Shift Alt V', 'Vložit jako Markdown'),
  // Free in the editor, Atlas and the browsers, no AltGr (Czech keyboard); Backspace right after an automatic chip also gives the text back.
  shortcut('Vkládání', 'Mod Shift Space', 'Převést čip na text (kontakt, úkol, stránka, emoji): vybraný nebo hned před kurzorem'),
  shortcut('Pohyb a výběr', 'Ctrl Space', 'Co s tím jde dělat: nabídka akcí u kurzoru nebo výběru (kontakt, úkol, text, AI); šipky, Enter, psaní filtruje'),
  shortcut('Pohyb a výběr', 'Alt Enter', 'Co s tím jde dělat, s návrhy AI nahoře (i na Macu, kde Ctrl+Mezerník přepíná vstup)'),
  shortcut('Pohyb a výběr', 'Mod .', 'Co s tím jde dělat (stejná nabídka)'),
  shortcut('Pohyb a výběr', 'Shift F10', 'Co s tím jde dělat (stejná nabídka; také klávesa nabídky a pravé tlačítko, Shift + pravé tlačítko nechá nabídku prohlížeče)'),
  shortcut('Pohyb a výběr', 'Enter', 'Na vybrané komponentě nebo čipu: upravit ji (stejně jako klik); otevřít je v nabídce akcí a Ctrl+klik'),
  shortcut('Pohyb a výběr', 'F2', 'Na vybrané komponentě: upravit ji'),
  shortcut('Pohyb a výběr', 'Escape', 'Zavřít nabídku, vrátit se do textu'),
  shortcut('Pohyb a výběr', 'Escape, Tab', 'Opustit editor'),
  shortcut('Pohyb a výběr', 'ArrowUp', 'Na začátku textu: zpět do názvu stránky'),
  shortcut('Pohyb a výběr', 'Mod /', 'Přehled klávesových zkratek'),
])

/** Markdown typed at the start of a line, shown in the overview next to the keys. */
export const markdownTypingShortcuts: readonly { readonly typed: string; readonly label: string }[] = Object.freeze([
  { typed: '# … ######', label: 'Nadpis 1–6' },
  { typed: '- nebo *', label: 'Odrážkový seznam' },
  { typed: '1.', label: 'Číslovaný seznam' },
  { typed: '[ ]', label: 'Úkol' },
  { typed: '>', label: 'Citace' },
  { typed: '> [!note], [!tip], [!warning], [!important], [!caution]', label: 'Informační box' },
  { typed: '```ts', label: 'Blok kódu s jazykem' },
  { typed: '---', label: 'Vodorovná čára' },
  { typed: '|Jméno|Telefon| a Enter', label: 'Tabulka' },
])

/** Bubbles from the editor on Ctrl+/ (⌘/); the host that shows its shortcut overview calls `preventDefault()`. */
export const MARKDOWN_SHORTCUT_HELP_EVENT = 'markdown-shortcut-help'
/** Bubbles from the editor on Up at the very start of the text and on Shift+Tab in its first block; a host with a field above (the page title) takes the caret and calls `preventDefault()`. */
export const MARKDOWN_LEAVE_START_EVENT = 'markdown-leave-start'
/** Dispatched on the editor's element by a host's link button: the selection bubble opens its link field. */
export const MARKDOWN_OPEN_LINK_EVENT = 'markdown-open-link'
/** Bubbles from the editor when the link's bar or its menu row opens a link (`detail.href`); a host that routes the address itself calls `preventDefault()`, otherwise it opens in a new tab. */
export const MARKDOWN_FOLLOW_LINK_EVENT = 'markdown-follow-link'
/** A transaction's meta when the block keys (Ctrl+Shift+Backspace, the grip's Smazat) removed a block: how many top-level blocks went
 *  (a heading takes its section along), so a host can say so when it is more than the one in view. */
export const MARKDOWN_BLOCK_REMOVAL_META = 'markdownBlockRemoval'


const appleNames: Readonly<Record<string, string>> = { Mod: '⌘', Alt: '⌥', Shift: '⇧', Ctrl: '⌃' }
const names: Readonly<Record<string, string>> = { ArrowUp: '↑', ArrowDown: '↓', ArrowLeft: '←', ArrowRight: '→', Escape: 'Esc', Backspace: 'Backspace', Space: 'Mezerník' }
/** The keys as the platform writes them: „Ctrl+Alt+1“, on Apple devices „⌘⌥1“. */
export function formatMarkdownShortcut(keys: readonly string[], apple = /Mac|iPhone|iPad|iPod/u.test(globalThis.navigator?.userAgent ?? '')): string {
  const parts = keys.map(key => (apple ? appleNames[key] : undefined) ?? names[key] ?? (key === 'Mod' ? 'Ctrl' : key))
  return apple ? parts.join('') : parts.join('+')
}
