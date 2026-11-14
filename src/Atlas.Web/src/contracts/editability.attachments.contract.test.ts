import { beforeAll, beforeEach, describe, vi } from "vitest";
import { loadMarkdownDialect, type MarkdownRichValue } from "@atlas/markdown/core";
import { runEditabilityContract } from "./editability";
import { settleModules } from "./editability.modules";

vi.setConfig({ testTimeout: 30_000 });
beforeAll(() => loadMarkdownDialect(), 60_000);
beforeEach(() => { vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null); });

const file = { version: 1, name: "Zpráva.pdf", url: "/files/report.pdf", size: 2048, mimeType: "application/pdf" };
const value = { format: "atlas-rich", version: 1, plugins: [{ id: "attachments", version: 1 }], document: { type: "doc", content: [{ type: "attachment", attrs: { data: file } }] } } as MarkdownRichValue;
const at = (doc: import("@tiptap/pm/model").Node, type: string) => {
  let found = -1;
  doc.descendants((node, pos) => { if (found < 0 && node.type.name === type) found = pos; return found < 0; });
  return found;
};

describe("příloha", () => {
  runEditabilityContract({
    name: "příloha",
    document: value,
    selector: '[data-md-module="attachment"]',
    settle: settleModules,
    // The error line and the file address are rows only an editor needs; the description and the name edit where they are shown.
    affordances: [".md-attachment-error", ".md-attachment-more"],
    act: ({ root }) => {
      root()!.querySelector<HTMLElement>(".md-attachment-name")!.click();
      const field = root()!.querySelector<HTMLInputElement>(".md-attachment-field");
      if (!field) return;
      field.value = "Nový název.pdf";
      field.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    },
    // The description field.
    open: async ({ root }) => { root()!.querySelector<HTMLElement>(".md-attachment-description")!.click(); return root()!.querySelector(".md-attachment-field"); },
    remote: editor => { const pos = at(editor.state.doc, "attachment"); return editor.state.tr.setNodeAttribute(pos, "data", { ...file, name: "Cizí.pdf" }); },
  });
});
