import { ref } from "vue";

/** The comment an address or a notification asked for (`#/pages/<id>?comment=<id>`): the page's thread reads up to it, scrolls to it and highlights it, then clears this. */
export const requestedComment = ref<string>();
