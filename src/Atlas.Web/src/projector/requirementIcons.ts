import { iconSvgMarkup, type MarkdownIconName } from "@atlas/markdown/icons";

// The deployment overview's icons as CSS masks over the text colour: one stylesheet for the page instead of an <svg> per cell, so a
// big matrix stays a few thousand elements. The drawings are the editor's one registry.
const names: readonly MarkdownIconName[] = ["clock", "hourglass", "loader-circle", "circle-check", "octagon-x", "ban", "user", "users", "arrow-down", "arrow-up",
  "calendar", "list-checks", "pencil", "book-open", "chevron-down", "x", "undo-2", "link", "plug", "plus", "file-text", "globe", "hash"];

const url = (svg: string) => `url("data:image/svg+xml,${encodeURIComponent(svg)}")`;
const mask = (name: MarkdownIconName) => url(iconSvgMarkup(name, 24));

/** Adds the icon rules to the document once; `<span class="rq-i" data-icon="calendar">` then draws the icon before its text. */
export function ensureRequirementIcons(doc: Document) {
  if (doc.querySelector("style[data-requirement-icons]")) return;
  const style = doc.createElement("style");
  style.dataset.requirementIcons = "";
  style.textContent = names.map(name => `.rq-i[data-icon="${name}"]{--rq-icon:${mask(name)}}`).join("")
    + `.rq-dir[data-direction="inbound"]{--rq-dir:${mask("arrow-down")}}.rq-dir[data-direction="outbound"]{--rq-dir:${mask("arrow-up")}}`;
  doc.head.append(style);
}
