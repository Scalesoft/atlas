import { beforeAll, beforeEach, describe, vi } from "vitest";
import { loadMarkdownDialect, type MarkdownRichValue } from "@atlas/markdown/core";
import { runEditabilityContract } from "./editability";
import { settleModules } from "./editability.modules";

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
import { contactNode, inlineContactNode, insertInlineContact } from "@atlas/markdown/plugins/contacts";

const jana = { name: "Jana Nováková", role: "PM projektu", organization: "Modelsoft", emails: ["jana@example.com"], phones: ["+420 777 111 222"] };
const karel = { name: "Karel Dvořák", organization: "Example", emails: ["karel@example.com"] };
const contacts = { id: "contacts", version: 2 };

/** The inline contact's chip opens the edit form of a writer (the reader's panel otherwise); the form is the open interaction. */
const openChip = async ({ root }: { root: () => Element | null }) => {
  await settleModules();
  const chip = root()!.querySelector<HTMLElement>("button.md-contact-name") ?? (root() as HTMLElement);
  return vi.waitFor(() => {
    chip.click();
    const panel = document.querySelector(".md-popover, .md-context-popover, [role=dialog]");
    if (!panel) throw new Error("no panel");
    return panel;
  });
};

describe("contact card", () => {
  runEditabilityContract({
    name: "karta kontaktu",
    document: page(contacts, contactNode(jana) as object, paragraph("Konec.")),
    selector: "article.md-contact-card",
    // A writer's click on the card opens its edit form.
    open: openChip,
    // The lazy contacts runtime has rendered before any check waits on its card or chip.
    settle: settleModules,
    act: ({ controller, editor }) => { editor.commands.setTextSelection(editor.state.doc.content.size - 1); return controller.dispatch(insertInlineContact(karel)); },
    remote: editor => { const pos = at(editor.state.doc, "contactCard"); return editor.state.tr.setNodeAttribute(pos, "data", { version: 1, contacts: [{ ...jana, role: "Vedoucí" }] }); },
    deviations: { switch: "The module host (features/modules/block.ts, core) restarts the runtime on an editability change and builds the card again; the host element stays." },
  });
});

describe("inline contact", () => {
  runEditabilityContract({
    name: "kontakt v textu",
    document: page(contacts, { type: "paragraph", content: [{ type: "text", text: "Ptejte se " }, inlineContactNode(jana) as object] }),
    selector: ".md-contact-card",
    open: openChip,
    // The lazy contacts runtime has rendered before any check waits on its card or chip.
    settle: settleModules,
    act: ({ controller, editor }) => { editor.commands.setTextSelection(editor.state.doc.content.size - 1); return controller.dispatch(insertInlineContact(karel)); },
    remote: editor => { const pos = at(editor.state.doc, "contactInline"); return editor.state.tr.setNodeAttribute(pos, "data", { version: 1, contacts: [{ ...jana, role: "Vedoucí" }] }); },
    deviations: { switch: "The module host (features/modules/block.ts, core) restarts the runtime on an editability change and builds the card again; the host element stays." },
  });
});
