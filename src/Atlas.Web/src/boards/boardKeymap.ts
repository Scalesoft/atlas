import type { EditorKeymapEntry } from "../keyboard/editorKeymap";
import type { BoardTool } from "../components/boards/canvas/boardCanvasContext";
import t from "../i18n/boards";
import { macCtrl } from "../keyboard/shortcuts";

/** The tools by their letter on the board's surface (spec §3.1); BoardCanvas reads them, the help below names them. */
export const boardToolKeys: Readonly<Record<string, BoardTool>> = { v: "select", h: "hand", n: "note", t: "text", s: "shape", f: "frame", l: "connector" };

// The words are read when the help is drawn (a getter), after the language's texts are in.
const row = (keys: string | (() => string), text: keyof typeof t.keymap.rows): EditorKeymapEntry => ({ get keys() { return macCtrl(typeof keys === "function" ? keys() : keys); }, get label() { return t.keymap.rows[text]; }, get group() { return t.keymap.group; } });

/**
 * The board's keymap for the help overlay („Tabule“, keyboard pass B2), in the shape of the editor's export (`editorKeymap.ts`).
 * The keys themselves are BoardCanvas's (onKeydown); keep the two in step. They work while the board's surface or an item on it
 * has the focus, not in a text being edited.
 */
export const boardKeymap: readonly EditorKeymapEntry[] = [
  row("V H N T S F L", "tools"),
  row("Enter", "insert"),
  row("P I C K", "pick"),
  row("/ Ctrl F", "commands"),
  row("Tab", "next"),
  row("Enter F2", "edit"),
  row("E", "lock"),
  row("Ctrl Enter", "open"),
  row(() => t.keymap.actionsKeys, "actions"),
  row("Esc", "esc"),
  row("Alt F10", "toolbar"),
  row("← → ↑ ↓", "nudge"),
  row("Alt ← → ↑ ↓", "resize"),
  row(() => t.keymap.connectKeys, "connect"),
  row("Ctrl A", "selectAll"),
  row("Ctrl D", "duplicate"),
  row("Ctrl G", "group"),
  row("Ctrl Shift G", "ungroup"),
  row("Delete", "delete"),
  row("Ctrl Z", "undo"),
  row("Ctrl Shift Z, Ctrl Y", "redo"),
  row("+ −", "zoom"),
  row("Shift 1", "fitAll"),
  row("Shift 2", "fitSelection"),
  row("Shift 0", "zoom100"),
  row("Ctrl '", "snap"),
  row(() => t.keymap.panKeys, "pan"),
  row("Ctrl /", "shortcuts"),
];
