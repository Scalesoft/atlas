import { ref } from "vue";
import { whenIdle } from "../../../idle";
import { lazyComponent, preloadComponent } from "../../../lazyComponent";

/** True once the editor chunk is in: until then a text being edited stays drawn by BoardMarkdown instead of going blank. */
export const textEditorReady = ref(false);

/** The loader of the editor chunk (tiptap, the page schema), for the `lazyComponent` in BoardText; it loads for the first edit or when an editable board idles. */
export const loadTextEditor = () => import("./BoardTextEditor.vue").then(module => { textEditorReady.value = true; return module; });

/**
 * The two lazy components of a board text, defined once for every node and edge (BoardText): a second edit finds the editor resolved.
 * Not in a graph that only reads: its nodes are page cards, so the text renderer (and the editor plugins) stay out of it.
 */
export const BoardTextEditor = lazyComponent(loadTextEditor);
export const BoardMarkdown = lazyComponent(() => import("./BoardMarkdown.vue"));

/**
 * Starts the download ahead of the first edit and resolves the editor component with it, so the first edit after it draws the editor
 * in the gesture's own task (P17.sync), as every later one does. A failed download is left to the edit itself: only a chunk that
 * arrived resolves the component (a failure would stand in for it for good).
 */
export const preheatTextEditor = () => {
  void import("./BoardTextEditor.vue").then(() => preloadComponent(BoardTextEditor), () => undefined);
  // The Markdown reader every editing starts from (one detached editor, a third of a first edit's cost), in a later idle moment.
  void import("../../../boards/editorBlocks").then(blocks => whenIdle(() => blocks.boardTextDocument("·")), () => undefined);
};
