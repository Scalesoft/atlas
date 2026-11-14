import { translate } from ".";

/**
 * Labels of the editor's keymap in the help („Editor“). The Czech words are the package's own (`markdownKeyboardShortcuts`,
 * `markdownTypingShortcuts` of @atlas/markdown/core), so this source stays empty: a language fills it by the keys of a row
 * („Mod Shift S“, a second step after „, “) or by the text typed („>“), which are stable whatever the label says; `typedKeys` words the Czech text shown as the keys.
 */
const cs = { keys: {} as Record<string, string>, typedKeys: {} as Record<string, string>, typed: {} as Record<string, string> };

export type KeyboardEditorMessages = typeof cs;
export default translate(cs, { en: () => import("./keyboard-editor.en") });
