import type { EditorKeymapEntry } from "../keyboard/editorKeymap";
import t from "../i18n/projector";

/**
 * The keys of requirement headings for the help overlay („Požadavky“), `{ keys, label, group }` as the editor's keymap. Only rows:
 * the keys themselves belong to the heading (requirementHeadingView.ts), its phrases (requirementInline.ts) and CatalogPicker. No
 * global key of its own (keyboard pass A1): Ctrl+Enter already sends a comment and inserts and goes on in the catalog.
 */
/** Read where the help shows it (getters), never at load: the language of the tool arrives after this module is evaluated. */
const entry = (key: keyof typeof t.keymap.entries): EditorKeymapEntry => ({
  get keys() { return t.keymap.entries[key].keys; },
  get label() { return t.keymap.entries[key].label; },
  get group() { return t.keymap.group; },
});

export const requirementKeymap: readonly EditorKeymapEntry[] = [
  entry("slashRequirement"),
  entry("slashModules"),
  entry("slashDeployment"),
  entry("waitingOn"),
  entry("deadline"),
  entry("menu"),
  entry("enterChip"),
  entry("pmActions"),
  entry("space"),
  entry("moveUp"),
  entry("catalogPick"),
  entry("catalogInsert"),
  entry("close"),
];
