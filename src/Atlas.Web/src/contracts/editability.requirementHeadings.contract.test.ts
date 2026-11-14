import { describe, vi } from "vitest";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { runEditabilityContract } from "./editability";
import { openStateMenu, toggleDone } from "../projector/requirementHeadingView";

/*
 * The editability contract (3.6-0) of a requirement heading (Atlas 3.6): the heading is the same read and written (its checkbox and
 * chips are editor widgets, the same boxes, inert while read), the state menu closes when the right goes, and a colleague's change of
 * state lands in the heading.
 */

const value = { format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 1 }, { id: "atlas-requirements", version: 4 }], document: { type: "doc", content: [
  { type: "heading", attrs: { level: 3, headingId: "nia", state: "waiting_for_input", direction: "inbound", system: "NIA" }, content: [{ type: "text", text: "Certifikát NIA" }] },
  { type: "paragraph", content: [{ type: "text", text: "od IT KBN · do 21. 7. 2026" }] },
] } } as MarkdownRichValue;

describe("requirement heading", () => {
  runEditabilityContract({
    name: "požadavek jako nadpis",
    document: value,
    selector: '[data-heading-id="nia"]',
    act: ({ editor }) => toggleDone(editor, "nia"),
    open: async ({ editor }) => {
      openStateMenu(editor, "nia");
      return vi.waitFor(() => { const menu = document.querySelector(".atlas-requirement-popover"); if (!menu) throw new Error("no state menu"); return menu; });
    },
    remote: editor => editor.state.tr.setNodeAttribute(0, "state", "done"),
  });
});
