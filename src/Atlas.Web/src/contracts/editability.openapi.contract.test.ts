import { beforeAll, beforeEach, describe, vi } from "vitest";
import { loadMarkdownDialect, type MarkdownRichValue } from "@atlas/markdown/core";
import { runEditabilityContract } from "./editability";
import { settleModules } from "./editability.modules";

vi.setConfig({ testTimeout: 30_000 });
beforeAll(() => loadMarkdownDialect(), 60_000);
beforeEach(() => {
  vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null);
  // jsdom has no modal dialog; the source dialog only needs to open and close.
  HTMLDialogElement.prototype.showModal = function () { this.setAttribute("open", ""); };
  HTMLDialogElement.prototype.close = function () { this.removeAttribute("open"); };
});

const api = { version: 1, filename: "openapi.yaml", source: "openapi: 3.1.0\ninfo:\n  title: Zakázky\n  version: '1.0'\npaths: {}\n" };
const value = { format: "atlas-rich", version: 1, plugins: [{ id: "openapi", version: 1 }], document: { type: "doc", content: [{ type: "openapi", attrs: { data: api } }] } } as MarkdownRichValue;
const at = (doc: import("@tiptap/pm/model").Node, type: string) => {
  let found = -1;
  doc.descendants((node, pos) => { if (found < 0 && node.type.name === type) found = pos; return found < 0; });
  return found;
};

describe("OpenAPI", () => {
  runEditabilityContract({
    name: "OpenAPI",
    document: value,
    selector: '[data-md-module="openapi"]',
    settle: settleModules,
    // The editor's extra rows of the card: the three modes, the spec actions and the error line.
    affordances: [".md-openapi-modes", ".md-openapi-edit", ".md-openapi-upload", ".md-openapi-card-error", "input[type=file]"],
    act: ({ root }) => { root()!.querySelector<HTMLElement>('.md-openapi-modes [data-mode="reference"]')?.click(); },
    // The source dialog of the spec.
    open: async ({ root }) => { root()!.querySelector<HTMLElement>(".md-openapi-edit")!.click(); return globalThis.document.querySelector(".md-openapi-source-dialog"); },
    remote: editor => { const pos = at(editor.state.doc, "openapi"); return editor.state.tr.setNodeAttribute(pos, "data", { ...api, filename: "cizi.yaml" }); },
  });
});
