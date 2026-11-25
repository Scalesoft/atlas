import { ref } from "vue";

/** The comment an address or a notification asked for (`#/pages/<id>?comment=<id>`): the page's thread reads up to it, scrolls to it and highlights it, then clears this. */
export const requestedComment = ref<string>();

/** The text a reader selected in the page when they asked for its Diskuse (Alt C): the thread that quotes (`quotes`) starts its new comment with it as a Markdown quote, then clears this. */
export const requestedQuote = ref<string>();

/** A selection as a Markdown quote that leaves the caret a line below it; a long selection is cut at a word, the quote is a pointer and not a copy. */
export function quoteMarkdown(selection: string, max = 300): string {
  const text = selection.replace(/\s+/g, " ").trim();
  if (!text) return "";
  const cut = text.length > max ? `${text.slice(0, max).replace(/\s+\S*$/, "")}…` : text;
  return `> ${cut}\n\n`;
}
