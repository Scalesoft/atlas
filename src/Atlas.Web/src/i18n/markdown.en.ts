import type { MarkdownStrings } from "@atlas/markdown/core";
import core from "./markdown-core.en";
import plugins from "./markdown-plugins.en";

/**
 * The editor's words in English (documentWidgets.ts passes them to `setMarkdownStrings`). Typed whole against the package's
 * `MarkdownStrings`: a key added to the editor without an English word fails typecheck. Sections live in the two files imported here.
 */
const en: MarkdownStrings = {
  locale: "en-US",
  apply: "Apply",
  cancel: "Cancel",
  close: "Close",
  delete: "Delete",
  remove: "Remove",
  loading: "Loading…",
  retry: "Retry",
  fullscreen: "Full screen",
  exitFullscreen: "Exit full screen",
  readOnly: "Read-only",
  insert: "Insert",
  markdownImportFailed: "The Markdown has a structure the document does not support and cannot be inserted.",
  pasteAsMarkdown: "Paste as Markdown",
  pasteAsMarkdownDescription: "Clipboard text as formatted content",
  clipboardReadFailed: "The clipboard could not be read. Paste the text with Ctrl+V.",
  ...core,
  ...plugins,
};
export default en;
