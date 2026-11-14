import { beforeAll, beforeEach, describe, vi } from "vitest";
import { loadMarkdownDialect, type MarkdownRichValue } from "@atlas/markdown/core";
import { runEditabilityContract } from "./editability";
import { settleModules } from "./editability.modules";

vi.setConfig({ testTimeout: 30_000 });
beforeAll(() => loadMarkdownDialect(), 60_000);
beforeEach(() => { vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null); });

const math = { version: 1, expression: "E = mc^2" };
const page = (...content: object[]) => ({ format: "atlas-rich", version: 1, plugins: [{ id: "math", version: 1 }], document: { type: "doc", content } }) as MarkdownRichValue;
const at = (doc: import("@tiptap/pm/model").Node, type: string) => {
  let found = -1;
  doc.descendants((node, pos) => { if (found < 0 && node.type.name === type) found = pos; return found < 0; });
  return found;
};
const type = (field: HTMLTextAreaElement, text: string) => { field.value = text; field.dispatchEvent(new Event("input", { bubbles: true })); };

describe("matematika", () => {
  runEditabilityContract({
    name: "vzorec (blok)",
    document: page({ type: "mathBlock", attrs: { data: math } }),
    selector: '[data-md-module="mathBlock"]',
    settle: settleModules,
    // The formula panel is hidden until it is opened: editors have it, readers do not.
    affordances: [".md-math-panel"],
    act: ({ root }) => { type(root()!.querySelector<HTMLTextAreaElement>("textarea")!, "a^2 + b^2 = c^2"); },
    // The formula panel: Enter on the block opens it.
    open: async ({ root }) => { root()!.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true })); return root()!.querySelector(".md-math-panel"); },
    remote: editor => { const pos = at(editor.state.doc, "mathBlock"); return editor.state.tr.setNodeAttribute(pos, "data", { ...math, expression: "x = 1" }); },
  });
});
