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
import { insertSecretReference, updateSecretReferencePresentation } from "@atlas/markdown/plugins/secrets";

const secretId = "0b9f6a52-7c1e-4d3a-9a58-3f2b1c4d5e6f";
const secrets = { id: "secrets", version: 4 };
const attrs = { secretId, field: "password", fields: null, showCopy: true, showDetails: false, autoHideSeconds: null };

describe("secret reference", () => {
  runEditabilityContract({
    name: "tajemství",
    document: page(secrets, { type: "secretReference", attrs }, paragraph("Konec.")),
    // The static render marks the block `data-md-secret`, the editor's view `data-md-secret-view`.
    selector: '[data-md-secret]:not([data-md-secret-inline]), [data-md-secret-view="block"]',
    services: { secrets: { discover: async () => "available", requestAccess: async () => {}, reveal: async () => { throw new Error("not in a contract test"); }, copy: async () => {}, subscribeRevocation: () => () => {} } },
    settle: () => new Promise(resolve => setTimeout(resolve, 30)),
    // The edit chips are an overlay below the card.
    affordances: [".md-secret-edit"],
    // The display settings: the „Další…“ chip on the card.
    open: async ({ root }) => vi.waitFor(() => {
      root()!.querySelector<HTMLElement>("[data-secret-chip=more]")!.click();
      const panel = document.querySelector(".md-secret-settings__form");
      if (!panel) throw new Error("no settings");
      return panel;
    }),
    act: ({ controller, editor }) => { editor.commands.setNodeSelection(at(editor.state.doc, "secretReference")); return controller.dispatch(updateSecretReferencePresentation({ showDetails: true })); },
    remote: editor => editor.state.tr.setNodeAttribute(at(editor.state.doc, "secretReference"), "showCopy", false),
  });
});
