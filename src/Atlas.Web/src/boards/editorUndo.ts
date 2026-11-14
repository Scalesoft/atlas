import { shallowRef } from "vue";

/**
 * The text editor being edited on the board (a note's text, a page's inline editor), for the board's „Zpět“ wedge: while one is, the
 * wedge undoes that editor's last step as Ctrl+Z in it does; otherwise it undoes the board's. One editing at a time (the canvas has one).
 */
export interface EditorUndo { readonly owner: object; readonly canUndo: boolean; undo(): void }
export const editorUndo = shallowRef<EditorUndo | null>(null);

export function setEditorUndo(owner: object, canUndo: boolean, undo: () => void) { editorUndo.value = { owner, canUndo, undo }; }
/** Only its own entry goes: a later editing may already have taken the place. */
export function clearEditorUndo(owner: object) { if (editorUndo.value?.owner === owner) editorUndo.value = null; }
