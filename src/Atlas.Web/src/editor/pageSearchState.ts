import { ref } from "vue";

/** Hledat ve stránce: what the reader typed, kept while the tab is away (Ctrl+F brings it back), and a request to focus the field.
 *  Apart from pageSearch.ts, so the shell sets them without loading the search (first-screen budget). */
export const pageSearchQuery = ref("");
export const pageSearchFocus = ref(0);
