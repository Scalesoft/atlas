import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import type { Editor } from "@tiptap/core";
import type { MarkdownPlugin, MarkdownRichValue } from "@atlas/markdown/core";
import { MarkdownEditor } from "@atlas/markdown/vue";
import { createYouTrackPlugin } from "@atlas/markdown/plugins/youtrack";
import { createHeadingAnchorsPlugin } from "@atlas/markdown/plugins/heading-anchors";
import { createDocumentWidgets } from "../documentWidgets";
import type { ProjectorHost } from "./requirementsPlugin";

// What typing in a requirement heading's title writes (plan 3.1 §2.3a): its facts line.
type Json = { type: string; attrs?: Record<string, unknown>; content?: Json[]; text?: string };
const p = (text = ""): Json => ({ type: "paragraph", ...(text ? { content: [{ type: "text", text }] } : {}) });

const toaster = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), dismissAll: vi.fn() }));
vi.mock("../composables/useToast", () => ({ useToast: () => toaster }));

const linking = vi.hoisted(() => ({ linkContact: vi.fn(async (contact: { name: string }) => ({ ...contact, objectId: "c9" })) }));
vi.mock("../contacts/contactLinking", async importOriginal => ({ ...(await importOriginal<object>()), linkContact: linking.linkContact }));
const learnAlias = vi.hoisted(() => vi.fn());
vi.mock("../contacts/contactSources", async importOriginal => ({ ...(await importOriginal<object>()), learnAlias }));
const people = vi.fn(async (_text: string, _signal: AbortSignal) => [] as unknown[]);
const host: ProjectorHost = { spaceId: () => "space-1", pageId: () => "page-1", open: vi.fn(), people: people as never,
  api: { catalog: vi.fn(async () => ({ definitions: [] })), definitions: vi.fn(), setCatalogPage: vi.fn(), createDeployment: vi.fn(), waiting: vi.fn(async () => ({ parties: [] })) } as unknown as ProjectorHost["api"] };

let wrapper: VueWrapper | undefined;
let editor!: Editor;
const probe: MarkdownPlugin = { id: "requirement-inline-probe", apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor; } }) };

async function open(value: MarkdownRichValue, extra: readonly MarkdownPlugin[] = []) {
  const model = ref(value);
  const plugins = [...createDocumentWidgets({ kind: "edit", projector: host }), createYouTrackPlugin({ client: { search: async () => [] } }), ...extra, probe];
  wrapper = mount(defineComponent({ setup: () => () => h(MarkdownEditor, { profile: "rich", modelValue: model.value,
    "onUpdate:modelValue": ((next: MarkdownRichValue) => { model.value = next; }) as never, plugins, ariaLabel: "Stránka" }) }), { attachTo: document.body });
  await vi.waitFor(() => expect(editor && !editor.isDestroyed && document.querySelector(".ProseMirror")).toBeTruthy());
  // The editor's „create“ (the blocks learn their host then) comes a task after the view.
  await new Promise(resolve => setTimeout(resolve));
}

const press = (key: string, init: KeyboardEventInit = {}) => editor.view.someProp("handleKeyDown", handler => handler(editor.view, new KeyboardEvent("keydown", { key, ...init }))) ?? false;
const type = (text: string) => {
  for (const char of text) {
    const { from, to } = editor.state.selection;
    if (!editor.view.someProp("handleTextInput", handler => handler(editor.view, from, to, char, () => editor.state.tr.insertText(char, from, to)))) editor.view.dispatch(editor.state.tr.insertText(char, from, to));
  }
};

beforeEach(() => { vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(new Date("2026-10-29T10:00:00Z")); });
afterEach(() => { wrapper?.unmount(); wrapper = undefined; editor = undefined as never; document.body.replaceChildren(); vi.useRealTimers(); vi.clearAllMocks(); });

describe("phrases in a requirement heading (Atlas 3.6): they write its facts line and party", () => {
  const heading = (id: string, title: string, attrs: Record<string, unknown> = {}): Json => ({ type: "heading", attrs: { level: 3, headingId: id, state: "planned", ...attrs }, content: [{ type: "text", text: title }] });
  const headingPage = (...content: Json[]): MarkdownRichValue =>
    ({ format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 1 }, { id: "atlas-requirements", version: 4 }], document: { type: "doc", content } } as MarkdownRichValue);
  const anchored = (...content: Json[]) => [headingPage(...content), [createHeadingAnchorsPlugin()]] as const;
  const blocks = () => (editor.getJSON().content as Json[]).map(node => node.type === "heading" ? `# ${node.content?.map(child => child.text).join("") ?? ""}:${node.attrs!.state}`
    : (node.content ?? []).map(child => child.text ?? `[${child.type}]`).join(""));
  const endOf = (title: string) => { editor.state.doc.forEach((node, pos) => { if (node.type.name === "heading" && node.textContent === title) editor.commands.setTextSelection(pos + node.nodeSize - 1); }); };

  it("„do pátku “ is the deadline in the facts line at once, made under the heading, the phrase gone; Ctrl+Z gives the text back", async () => {
    await open(...anchored(heading("ssl", "SSL"), p("Poznámka."), heading("vpn", "VPN", { direction: "inbound", party: "Petra Malá" }), p("Konec.")));
    endOf("SSL");
    type(" do pátku ");
    expect(blocks()).toEqual(["# SSL:planned", "do 30. 10. 2026", "Poznámka.", "# VPN:planned", "Konec."]);
    // The party is the heading's attribute: the facts line holds the date alone.
    endOf("VPN");
    type(" do 15. 11. ");
    expect(blocks().slice(3)).toEqual(["# VPN:planned", "do 15. 11. 2026", "Konec."]);
    expect(editor.state.doc.child(3).attrs.party).toBe("Petra Malá");
    editor.commands.undo();
    expect(blocks()[3]).toMatch(/^# VPN do 15\. 11\. ?:planned$/);
    expect(blocks()).toHaveLength(5);
    expect(editor.state.doc.child(3).attrs.party).toBe("Petra Malá");
  });

  it("„do pátku“ and Enter: the date first, then Enter's new paragraph after the facts line", async () => {
    await open(...anchored(heading("ssl", "SSL"), p("Poznámka.")));
    endOf("SSL");
    type(" do pátku");
    expect(press("Enter")).toBe(true);
    expect(blocks()).toEqual(["# SSL:planned", "do 30. 10. 2026", "", "Poznámka."]);
    expect(editor.state.selection.$head.index(0)).toBe(2);
  });

  it("„čekáme na X“ is the party of the facts line and the state čeká na vstup, in one step", async () => {
    await open(...anchored(heading("ssl", "SSL", { direction: "inbound", party: "Petra Malá" }), p("do 15. 11. 2026"), heading("vpn", "VPN", { direction: "inbound" }), p("do 1. 12. 2026")));
    endOf("VPN");
    type(" čekáme na Pet");
    await vi.waitFor(() => expect(document.querySelector("[data-testid=requirement-party-trigger] [role=option]")?.textContent).toContain("Petra Malá"));
    expect(press("Enter")).toBe(true);
    expect(blocks().slice(2)).toEqual(["# VPN:waiting_for_input", "do 1. 12. 2026"]);
    // The party goes to the heading's attributes, nothing to the facts line.
    expect(editor.state.doc.child(2).attrs).toMatchObject({ state: "waiting_for_input", party: "Petra Malá" });
    editor.commands.undo();
    expect(blocks().slice(2)).toEqual(["# VPN čekáme na Pet:planned", "do 1. 12. 2026"]);
    expect(editor.state.doc.child(2).attrs).toMatchObject({ state: "planned", party: "" });
  });

  it("Ctrl+Alt+M anywhere in the section adds a dated line on top of its history list, the caret after the stamp", async () => {
    // The list goes after the last line written, above the blank lines kept before the next heading.
    await open(...anchored(heading("ssl", "SSL"), p("Poznámka."), p(), p(), heading("vpn", "VPN")));
    editor.state.doc.forEach((node, pos) => { if (node.textContent === "Poznámka.") editor.commands.setTextSelection(pos + 3); });
    expect(press("m", { ctrlKey: true, altKey: true })).toBe(true);
    type("Volali jsme.");
    const list = (editor.getJSON().content as Json[])[2]!;
    // The editor's timed list, as the page reads a dated list back.
    expect(list.type).toBe("timedList");
    expect(list.content![0]!.content![0]!.content![0]!.text).toMatch(/^29\. 10\. 2026 \d{1,2}:\d{2} — Volali jsme\.$/);
    // The blank lines, then the next heading (the editor's trailing paragraph after a final heading is not the section's).
    expect((editor.getJSON().content as Json[]).slice(3, 6).map(node => node.type)).toEqual(["paragraph", "paragraph", "heading"]);
    // A second „Komentář“ goes on top of the same list, not into a second one.
    expect(press("m", { ctrlKey: true, altKey: true })).toBe(true);
    expect((editor.getJSON().content as Json[]).filter(node => node.type === "timedList").map(node => node.content!.length)).toEqual([2]);
  });
});
