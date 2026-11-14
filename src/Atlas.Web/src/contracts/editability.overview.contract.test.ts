import { describe } from "vitest";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { runEditabilityContract } from "./editability";
import { setHeadingState } from "../projector/requirementHeadingView";

/*
 * The editability contract (3.6-0) of the requirements overview (Přehled): the card of the page's live index is the same read and
 * written, and a requirement's change, mine or a colleague's, lands in it at once. Its pin and menu need a page host (the projector
 * host) and are affordances only while the page writes.
 */

const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
const heading = { type: "heading", attrs: { level: 3, headingId: "nia", state: "waiting_for_input", direction: "inbound" }, content: [{ type: "text", text: "Certifikát NIA" }] };
const value = { format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 1 }, { id: "atlas-requirements", version: 4 }],
  document: { type: "doc", content: [{ type: "overview", attrs: { id: "5d1b8a3e-2c47-4e9f-8a60-7b3c9d2e1f40", kind: "requirements" } }, heading, paragraph("od IT KBN · do 21. 7. 2026")] } } as MarkdownRichValue;

describe("requirements overview", () => {
  runEditabilityContract({
    name: "přehled požadavků",
    document: value,
    selector: "[data-overview]",
    act: ({ editor }) => setHeadingState(editor, "nia", "done"),
    remote: editor => { let pos = -1; editor.state.doc.forEach((node, at) => { if (pos < 0 && node.attrs.state) pos = at; }); return editor.state.tr.setNodeAttribute(pos, "state", "done"); },
    deviations: { render: "The contract's page is headless (no Vue host, as the node view's own fallback): the overview block draws its one line, the static render draws the card. The card's geometry is the gallery's „Upravovat“ switch." },
  });
});
