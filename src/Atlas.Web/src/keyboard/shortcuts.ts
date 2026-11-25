/**
 * The one keyboard model of Atlas: every
 * application shortcut is a row here, App.vue listens once and runs the row by its id, and the help overlay lists the same
 * rows. Widgets keep their own keys (a tree's arrows, a menu's letters, the editor's and a board's keymaps); a single-letter
 * shortcut never fires inside them or in a field.
 */
import type { EditorKeymapEntry } from "./editorKeymap";
import t from "../i18n/shell";

/** One line of the help: the same shape as the editor's exported keymap. */
export type ShortcutEntry = EditorKeymapEntry;

export type AppShortcutId =
  | "search" | "commands" | "searchView" | "assistant" | "help" | "helpAlt"
  | "newPage" | "edit" | "save" | "findInPage" | "discussion" | "treeFilter"
  | "goLibrary" | "goRecent" | "goFavorites" | "goDrafts" | "goContacts" | "goPersonal" | "goShared" | "goNotifications" | "goSettings" | "goProfile";

export interface AppShortcut {
  id: AppShortcutId;
  /**
   * Steps separated by a space („g k“: G, then K). A step is a key with `Mod+` (Ctrl, ⌘ on a Mac) and `Shift+`; `?` and `/`
   * are matched by the character, whatever the layout needs to type it.
   */
  chord: string;
  label: string;
  group: HelpGroup;
  /**
   * Where it works: `anywhere` also in fields and the editor (Ctrl+K), `outsideEditor` also in fields but not in the editor,
   * `outsideFields` (the default for keys without Ctrl) only where nothing takes the letters itself.
   */
  where?: "anywhere" | "outsideEditor" | "outsideFields";
}

type HelpGroup = "Kdekoli" | "Přejít na" | "Stránka";

export const appShortcuts: readonly AppShortcut[] = [
  { id: "search", chord: "Mod+k", get label() { return t.kb.app.search; }, group: "Kdekoli", where: "anywhere" },
  { id: "commands", chord: "Mod+Shift+p", get label() { return t.kb.app.commands; }, group: "Kdekoli", where: "anywhere" },
  { id: "searchView", chord: "Mod+Shift+Enter", get label() { return t.kb.app.searchView; }, group: "Kdekoli", where: "anywhere" },
  { id: "assistant", chord: "Mod+j", get label() { return t.common.assistant; }, group: "Kdekoli", where: "anywhere" },
  { id: "help", chord: "?", get label() { return t.common.keyboardShortcuts; }, group: "Kdekoli" },
  { id: "helpAlt", chord: "Mod+/", get label() { return t.kb.app.helpAlt; }, group: "Kdekoli", where: "outsideEditor" },
  { id: "treeFilter", chord: "Mod+Shift+f", get label() { return t.kb.app.treeFilter; }, group: "Kdekoli", where: "outsideEditor" },
  { id: "goLibrary", chord: "g k", get label() { return t.common.library; }, group: "Přejít na" },
  { id: "goRecent", chord: "g n", get label() { return t.common.recent; }, group: "Přejít na" },
  { id: "goFavorites", chord: "g o", get label() { return t.common.favorites; }, group: "Přejít na" },
  { id: "goDrafts", chord: "g m", get label() { return t.common.myDrafts; }, group: "Přejít na" },
  { id: "goContacts", chord: "g c", get label() { return t.cmd.contacts; }, group: "Přejít na" },
  { id: "goPersonal", chord: "g u", get label() { return t.common.mySpace; }, group: "Přejít na" },
  { id: "goShared", chord: "g d", get label() { return t.common.sharedWithMe; }, group: "Přejít na" },
  { id: "goNotifications", chord: "g i", get label() { return t.common.notifications; }, group: "Přejít na" },
  { id: "goSettings", chord: "g s", get label() { return t.common.settings; }, group: "Přejít na" },
  { id: "goProfile", chord: "g p", get label() { return t.cmd.profileAppearance; }, group: "Přejít na" },
  { id: "newPage", chord: "n", get label() { return t.kb.app.newPage; }, group: "Stránka" },
  { id: "edit", chord: "e", get label() { return t.kb.app.edit; }, group: "Stránka" },
  { id: "save", chord: "Mod+s", get label() { return t.common.saveVersion; }, group: "Stránka", where: "anywhere" },
  // On an open page also in its editor, like Ctrl+K and Ctrl+S (the editor's Najít a nahradit is a button of the panel).
  // Alt C: the page's Diskuse in the dock (the selected text becomes the quote of a new comment); in the editor too, to take the selection.
  { id: "discussion", chord: "Alt+c", get label() { return t.kb.app.discussion; }, group: "Stránka", where: "anywhere" },
  { id: "findInPage", chord: "Mod+f", get label() { return t.kb.app.findInPage; }, group: "Stránka", where: "anywhere" },
];

/** Keys every list, tree, menu and dialog answers to; they belong to the widgets, the help only names them. */
const widgetKeys = (): readonly ShortcutEntry[] => [
  { keys: "↑ ↓", label: t.kb.w.prevNext, group: "Seznamy, stromy a nabídky" },
  { keys: "Home End", label: t.kb.w.firstLast, group: "Seznamy, stromy a nabídky" },
  { keys: "→ ←", label: t.kb.w.treeExpand, group: "Seznamy, stromy a nabídky" },
  { keys: t.kb.w.letters, label: t.kb.w.lettersText, group: "Seznamy, stromy a nabídky" },
  { keys: "Alt ↑ ↓", label: t.kb.w.moveSiblings, group: "Seznamy, stromy a nabídky" },
  { keys: "Alt → ←", label: t.kb.w.indent, group: "Seznamy, stromy a nabídky" },
  { keys: "Ctrl Space", label: t.kb.w.actions, group: "Seznamy, stromy a nabídky" },
  { keys: "F2", label: t.kb.w.rename, group: "Seznamy, stromy a nabídky" },
  { keys: "Delete", label: t.kb.w.trash, group: "Seznamy, stromy a nabídky" },
  { keys: "Ctrl Enter", label: t.kb.w.openBoard, group: "Seznamy, stromy a nabídky" },
  { keys: "Enter", label: t.kb.w.enter, group: "Seznamy, stromy a nabídky" },
  { keys: "Tab", label: t.kb.w.tab, group: "Seznamy, stromy a nabídky" },
  { keys: "Esc", label: t.kb.w.esc, group: "Seznamy, stromy a nabídky" },
  { keys: t.kb.w.upKeys, label: t.kb.w.upToTitle, group: "Stránka" },
  { keys: "Enter", label: t.kb.w.toBody, group: "Stránka" },
  { keys: "Esc", label: t.kb.w.titleEsc, group: "Stránka" },
  // Obsah of a space and the graph of an object's relations (components/SpacePageList.vue, objects/RelationsGraph.vue).
  { keys: t.kb.w.contentSearchKeys, label: t.kb.w.contentSearchText, group: "Seznamy, stromy a nabídky" },
  { keys: t.kb.w.contentRowKeys, label: t.kb.w.contentRowText, group: "Seznamy, stromy a nabídky" },
  { keys: t.kb.w.graphKeys, label: t.kb.w.graphText, group: "Seznamy, stromy a nabídky" },
  { keys: t.kb.w.graphShiftKeys, label: t.kb.w.graphShiftText, group: "Seznamy, stromy a nabídky" },
  { keys: t.kb.w.graphEscKeys, label: t.kb.w.graphEscText, group: "Seznamy, stromy a nabídky" },
];

const isMac = () => typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.platform);

/** The chord as the help and the hints write it: „Ctrl K“, „Ctrl Shift P“, „G, pak K“. */
export function chordLabel(chord: string): string {
  return chord.split(" ").map(step => step.split("+").map(part =>
    part === "Mod" ? (isMac() ? "⌘" : "Ctrl") : part === "Alt" && isMac() ? "⌥" : part.length === 1 ? part.toUpperCase() : part).join(" ")).join(`${t.kb.then} `);
}

export interface HelpSection { title: string; entries: readonly ShortcutEntry[] }

/**
 * The help overlay's sections in order; the editor's own keymap (C's export), the requirement blocks' keys (Projector, while the
 * module is on) and the board's (B2) come last, each when there is one.
 */
export function helpSections(editorKeymap: readonly ShortcutEntry[], requirementKeymap: readonly ShortcutEntry[] = [], boardKeymap: readonly ShortcutEntry[] = [], ai = true): HelpSection[] {
  // AI is extra: while nothing can answer, the help neither lists Ctrl J nor names AI in a row of the editor (the flag that hides the AI entry points).
  const withoutAi = (entry: ShortcutEntry): ShortcutEntry => ai ? entry : { ...entry, label: entry.label.replace(/, AI\)/, ")").replace(/, (?:s návrhy AI nahoře|with AI suggestions on top)/, "") };
  const app = appShortcuts.filter(item => ai || item.id !== "assistant").map(item => ({ keys: chordLabel(item.chord), label: item.label, group: item.group as string }));
  const all = [...app, ...widgetKeys()];
  const groupTitle = (id: string) => ({ "Kdekoli": t.kb.groupAnywhere, "Přejít na": t.kb.groupGoTo, "Stránka": t.kb.groupPage, "Seznamy, stromy a nabídky": t.kb.groupLists } as Record<string, string>)[id] ?? id;
  const titles = ["Kdekoli", "Přejít na", "Stránka", "Seznamy, stromy a nabídky"];
  const sections = titles.map(title => ({ title: groupTitle(title), entries: all.filter(entry => entry.group === title) }));
  const own = [{ title: t.kb.editor, entries: editorKeymap.map(withoutAi) }, { title: t.kb.requirements, entries: requirementKeymap }, { title: t.kb.boards, entries: boardKeymap }].filter(section => section.entries.length);
  return [...sections, ...own];
}

/** A field, the editor, or a widget that uses plain letters itself (type-ahead, a board's tools). */
export function ownsLetters(target: EventTarget | null): boolean {
  if (!(target instanceof Element)) return false;
  if (target.closest('input, textarea, select, [contenteditable="true"], [contenteditable=""], [role="textbox"]:not([contenteditable="false"]), [role="combobox"]')) return true;
  return !!target.closest('[role="tree"], [role="menu"], [role="listbox"], [role="grid"], [data-board-surface], [data-own-keys]');
}
const inEditor = (target: EventTarget | null) => target instanceof Element && !!target.closest('[contenteditable="true"], [contenteditable=""]');

interface Step { key: string; mod: boolean; shift: boolean; alt: boolean }
function parseStep(step: string): Step {
  const parts = step.split("+");
  const key = parts.at(-1)!;
  return { key: key.toLowerCase(), mod: parts.includes("Mod"), shift: parts.includes("Shift"), alt: parts.includes("Alt") };
}
function stepMatches(step: Step, event: KeyboardEvent): boolean {
  // An Alt chord is Alt alone, by the physical key (Alt+C types „ç“ on a Mac); AltGr is Ctrl+Alt on Windows and writes characters.
  if (step.alt) return event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey && event.code === `Key${step.key.toUpperCase()}`;
  if (event.altKey) return false;
  const mod = event.ctrlKey || event.metaKey;
  if (mod !== step.mod) return false;
  const key = event.key.toLowerCase();
  if (step.key === "?" || step.key === "/") return key === step.key || (step.key === "/" && event.code === "Slash" && !event.shiftKey);
  return key === step.key && event.shiftKey === step.shift;
}

/**
 * One matcher for the window's single listener: the shortcut a key press completes, or undefined. A first step of a sequence
 * (G) is remembered for `sequenceMs`; any other key forgets it.
 */
export function createShortcutMatcher(shortcuts: readonly AppShortcut[], options: { sequenceMs?: number } = {}) {
  const sequenceMs = options.sequenceMs ?? 1500;
  const parsed = shortcuts.map(shortcut => ({ shortcut, steps: shortcut.chord.split(" ").map(parseStep) }));
  let pending: { step: number; candidates: typeof parsed; at: number } | undefined;
  const allowed = (shortcut: AppShortcut, steps: Step[], target: EventTarget | null) => {
    // A code editor (the OpenAPI source) has its own find; the page's side panel would pull the keyboard out from under it.
    if (shortcut.id === "findInPage" && target instanceof Element && target.closest(".monaco-editor")) return false;
    const where = shortcut.where ?? (steps[0]!.mod ? "anywhere" : "outsideFields");
    return where === "anywhere" || (where === "outsideEditor" ? !inEditor(target) : !ownsLetters(target));
  };
  return (event: KeyboardEvent, now = performance.now()): AppShortcut | undefined => {
    if (event.isComposing || ["Shift", "Control", "Alt", "Meta"].includes(event.key)) return undefined;
    const sequence = pending && now - pending.at <= sequenceMs ? pending : undefined;
    pending = undefined;
    if (sequence) {
      const done = sequence.candidates.find(({ steps }) => stepMatches(steps[sequence.step]!, event));
      if (done) return done.shortcut;
    }
    const started = parsed.filter(({ shortcut, steps }) => stepMatches(steps[0]!, event) && allowed(shortcut, steps, event.target));
    const single = started.find(({ steps }) => steps.length === 1);
    if (single) return single.shortcut;
    if (started.length) pending = { step: 1, candidates: started, at: now };
    return undefined;
  };
}
