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

import { insertPageReference } from "@atlas/markdown/plugins/pages";

const target = { id: "page-1", title: "Nasazení", url: "/?page=page-1" };

describe("page links", () => {
  runEditabilityContract({
    name: "odkaz na stránku",
    document: page({ id: "pages", version: 1 }, { type: "paragraph", content: [{ type: "text", text: "Viz " }, { type: "pageReference", attrs: target }] }),
    selector: "a[data-page-reference]",
    act: ({ controller, editor }) => { editor.commands.setTextSelection(editor.state.doc.content.size - 1); return controller.dispatch(insertPageReference({ id: "page-2", title: "Provoz", url: "/?page=page-2" })); },
    remote: editor => { const pos = at(editor.state.doc, "pageReference"); return editor.state.tr.setNodeAttribute(pos, "title", "Přejmenováno"); },
  });
});
