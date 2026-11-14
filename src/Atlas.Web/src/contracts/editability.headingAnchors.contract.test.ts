import { beforeAll, beforeEach, describe, vi } from "vitest";
import { loadMarkdownDialect, type MarkdownRichValue } from "@atlas/markdown/core";
import { runEditabilityContract } from "./editability";

beforeAll(() => loadMarkdownDialect(), 60_000);
beforeEach(() => { vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null); });

const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
const page = (plugin: { id: string; version: number }, ...content: object[]) => ({ format: "atlas-rich", version: 1, plugins: [plugin], document: { type: "doc", content } }) as MarkdownRichValue;
/** The position of the first node of `type`. */
const at = (doc: import("@tiptap/pm/model").Node, type: string) => {
  let found = -1;
  doc.descendants((node, pos) => { if (found < 0 && node.type.name === type) found = pos; return found < 0; });
  return found;
};

describe("heading anchors", () => {
  runEditabilityContract({
    name: "kotvy nadpisů",
    document: page({ id: "heading-anchors", version: 1 }, { type: "heading", attrs: { level: 2, headingId: "intro-1" }, content: [{ type: "text", text: "Úvod" }] }, paragraph("Text.")),
    selector: "h2",
    // A new heading takes its identity from the plugin only where the page is written.
    act: ({ controller, editor }) => { editor.commands.setTextSelection(editor.state.doc.content.size - 2); return controller.dispatch({ type: "set-heading", level: 3 }); },
    remote: editor => { const pos = at(editor.state.doc, "heading"); return editor.state.tr.setNodeAttribute(pos, "level", 3); },
  });
});
