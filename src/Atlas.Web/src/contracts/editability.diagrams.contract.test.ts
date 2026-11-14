import { beforeAll, beforeEach, describe, vi } from "vitest";
import { loadMarkdownDialect, type MarkdownRichValue } from "@atlas/markdown/core";
import { runEditabilityContract } from "./editability";
import { settleModules } from "./editability.modules";

// Mermaid draws nothing in jsdom (no layout, images never decode); a failing render gives the same readable fallback in every state.
vi.mock("mermaid", () => ({ default: { initialize: vi.fn(), render: vi.fn().mockRejectedValue(new Error("Syntax error")) } }));
vi.setConfig({ testTimeout: 30_000 });
beforeAll(() => loadMarkdownDialect(), 60_000);
beforeEach(() => { vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null); });

const diagram = { version: 1, source: "flowchart LR\n  A[Začátek] --> B[Konec]", caption: "Postup" };
const value = { format: "atlas-rich", version: 1, plugins: [{ id: "diagrams", version: 2 }], document: { type: "doc", content: [{ type: "diagram", attrs: { data: diagram } }] } } as MarkdownRichValue;
const at = (doc: import("@tiptap/pm/model").Node, type: string) => {
  let found = -1;
  doc.descendants((node, pos) => { if (found < 0 && node.type.name === type) found = pos; return found < 0; });
  return found;
};

describe("diagram", () => {
  runEditabilityContract({
    name: "diagram",
    document: value,
    selector: '[data-md-module="diagram"]',
    settle: settleModules,
    // The caption typed in the source panel: every keystroke is the node's typing step.
    act: ({ root }) => {
      const field = root()!.querySelector<HTMLInputElement>(".md-diagram-source-editor input")!;
      field.value = "Nový popisek";
      field.dispatchEvent(new Event("input", { bubbles: true }));
    },
    // The source panel: Enter on the block opens it.
    open: async ({ root }) => {
      root()!.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
      return root()!.querySelector(".md-diagram-source-editor");
    },
    remote: editor => { const pos = at(editor.state.doc, "diagram"); return editor.state.tr.setNodeAttribute(pos, "data", { ...diagram, caption: "Cizí popisek" }); },
  });
});
