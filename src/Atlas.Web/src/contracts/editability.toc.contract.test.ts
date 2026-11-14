import { beforeAll, beforeEach, describe, vi } from "vitest";
import { loadMarkdownDialect, type MarkdownRichValue } from "@atlas/markdown/core";
import { runEditabilityContract } from "./editability";
import { settleModules } from "./editability.modules";

vi.setConfig({ testTimeout: 30_000 });
beforeAll(() => loadMarkdownDialect(), 60_000);
beforeEach(() => { vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null); });

const toc = { version: 1, title: "Obsah dokumentu", minLevel: 1, maxLevel: 3 };
const heading = (level: number, text: string) => ({ type: "heading", attrs: { level }, content: [{ type: "text", text }] });
const value = { format: "atlas-rich", version: 1, plugins: [{ id: "toc", version: 1 }], document: { type: "doc", content: [{ type: "tableOfContents", attrs: { data: toc } }, heading(1, "Úvod"), heading(2, "Podrobnosti")] } } as MarkdownRichValue;
const at = (doc: import("@tiptap/pm/model").Node, type: string) => {
  let found = -1;
  doc.descendants((node, pos) => { if (found < 0 && node.type.name === type) found = pos; return found < 0; });
  return found;
};

describe("obsah", () => {
  runEditabilityContract({
    name: "obsah",
    document: value,
    selector: '[data-md-module="tableOfContents"]',
    settle: settleModules,
    // The range chip is the header's one extra control (laid over its end); the title is the same element, edited where it is shown.
    affordances: [".md-toc-range-wrap"],
    act: ({ root }) => {
      root()!.querySelector<HTMLElement>(".md-toc-title")!.click();
      const input = root()!.querySelector<HTMLInputElement>(".md-toc-title-input");
      if (!input) return;
      input.value = "Osnova";
      input.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    },
    // The range menu on the chip.
    open: async ({ root }) => { root()!.querySelector<HTMLElement>(".md-toc-range")!.click(); const menu = document.querySelector('[role=menu]'); if (!menu) throw new Error("the range menu did not open"); return menu; },
    remote: editor => { const pos = at(editor.state.doc, "tableOfContents"); return editor.state.tr.setNodeAttribute(pos, "data", { ...toc, title: "Cizí obsah" }); },
  });
});
