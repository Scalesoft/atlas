import { formatMarkdownShortcut, markdownKeyboardShortcuts, markdownTypingShortcuts, type MarkdownShortcut } from "@atlas/markdown/core";
import t from "../i18n/shell";
import shellMore from "../i18n/shell-more";
import texts from "../i18n/keyboard-editor";

/**
 * The page editor's keymap for the help overlay („Editor“): one row per key of the editor (`markdownKeyboardShortcuts` of
 * `@atlas/markdown/core`, Czech, the same words as its menus and tooltips), then the Markdown typed at the start of a line
 * (`markdownTypingShortcuts`), `{ keys, label, group }`. A page has no formatting row (3.6, one editor): these, the selection's bubble
 * (Alt+F10) and `/` are the ways to every block and mark, callouts and tables included.
 */
export interface EditorKeymapEntry {
  /** As the help writes it: „Ctrl B“, „Ctrl Shift 7“; a second step after „, pak “. */
  keys: string;
  label: string;
  group: string;
}

const step = (keys: readonly string[]) => formatMarkdownShortcut(keys).split("+").join(" ");

const keysOf = (shortcut: MarkdownShortcut) => [...shortcut.keys, ...(shortcut.then ? [",", ...shortcut.then] : [])].join(" ").replace(" , ", ", ");

// On a page Ctrl+F is Atlas's Hledat ve stránce (shortcuts.ts, its own row of the help); the editor's search is reached from it.
export const editorKeymap: readonly EditorKeymapEntry[] = markdownKeyboardShortcuts.filter(shortcut => keysOf(shortcut) !== "Mod F").map((shortcut): EditorKeymapEntry => ({
  get keys() { return shortcut.then ? `${step(shortcut.keys)}${t.kb.then} ${step(shortcut.then)}` : step(shortcut.keys); },
  get label() { return texts.keys[keysOf(shortcut)] ?? shortcut.label; },
  group: shortcut.group,
})).concat(markdownTypingShortcuts.map((shortcut): EditorKeymapEntry => ({
  get keys() { return texts.typedKeys[shortcut.typed] ?? shortcut.typed; },
  get label() { return shellMore.kb.typedAtLineStart(texts.typed[shortcut.typed] ?? shortcut.label); },
  get group() { return shellMore.kb.typingMarkdown; },
})));
