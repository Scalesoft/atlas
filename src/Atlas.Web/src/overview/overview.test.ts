import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import { Editor } from "@tiptap/core";
import Document from "@tiptap/extension-document";
import Paragraph from "@tiptap/extension-paragraph";
import Text from "@tiptap/extension-text";
import { nextTick } from "vue";
import { createMarkdownEditorController, loadMarkdownDialect, parseMarkdownContent, type MarkdownPlugin } from "@atlas/markdown/core";
import { createDocumentWidgets } from "../documentWidgets";
import { emptyDocument } from "../document";
import PageContactsCard from "../components/contacts/PageContactsCard.vue";
import { cardRequirementNodes } from "../projector/deploymentMap";
import { documentIssues, issueKeys, issuesCsv } from "../youtrack/issueOverview";
import { boardDocument, overviewKind, overviewKinds, overviewLevel, registerOverviewKind, setOverviewLevel, watchOverviewLevels } from "./overviewKinds";
import { OverviewNode, readOverviewFence, touchesOverview } from "./overviewNode";
import OverviewReadCard from "./OverviewReadCard.vue";

const fixture = (name: string) => JSON.parse(readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), `../../../../tests/fixtures/content/${name}`), "utf8"));
const issue = (key: string, state = "Open") => ({ type: "youtrackIssue", attrs: { id: `id-${key}`, key, title: `Úkol ${key}`, url: `https://yt.example.com/issue/${key}`, state } });

beforeAll(() => loadMarkdownDialect(), 60_000);
afterEach(() => { localStorage.clear(); });

describe("the overview's Markdown (the dialect's a-overview fence)", () => {
  let editor: Editor | undefined;
  const probe: MarkdownPlugin = { id: "overview-probe", apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor; } }) };
  const open = (content: unknown[]) => {
    const controller = createMarkdownEditorController({ profile: "rich", source: { kind: "local", value: emptyDocument() },
      plugins: [...createDocumentWidgets({ kind: "history" }), probe] });
    controller.attachView(document.createElement("div"), { ariaLabel: "Přehled" });
    editor!.commands.setContent({ type: "doc", content } as never);
    return controller;
  };

  it("writes a kind without a line as the fence and reads it back as the same overviews; requirements keep their readable line", () => {
    const controller = open([{ type: "overview", attrs: { kind: "contacts" } }, { type: "overview", attrs: { kind: "issues" } }, { type: "overview", attrs: { kind: "requirements" } }]);
    try {
      const markdown = controller.getProjection().markdown;
      expect(markdown).toBe("```a-overview\nkind: contacts\n```\n\n```a-overview\nkind: issues\n```\n\n**Stav nasazení:** 0 % hotovo (0 z 0)");
      const read = parseMarkdownContent(editor!, "```a-overview\nkind: contacts\n```\n\n```a-overview\n# přehled úkolů\nkind: issues\n```\n\n```a-overview\nkind: requirements\n```").toJSON();
      expect(read.content.map((node: { type: string; attrs?: { kind?: string } }) => `${node.type}:${node.attrs?.kind ?? ""}`))
        .toEqual(["overview:contacts", "overview:issues", "overview:requirements"]);
    } finally { controller.destroy(); }
  });

  it("redraws the editor's contacts card only when the people changed", async () => {
    const person = (name: string) => ({ type: "contactCard", attrs: { data: { version: 1, contacts: [{ name, emails: [`${name}@example.cz`] }] } } });
    const controller = open([person("jana"), { type: "paragraph" }]);
    const contacts = mount(PageContactsCard, { props: { editor: editor! } });
    try {
      const listed = () => (contacts.vm as unknown as { live: unknown[] }).live, before = listed();
      editor!.commands.insertContentAt(editor!.state.doc.content.size - 1, "poznámka");
      expect(listed()).toBe(before);
      editor!.commands.insertContentAt(editor!.state.doc.content.size, person("petr"));
      expect(listed()).toHaveLength(2);
    } finally { contacts.unmount(); controller.destroy(); }
  });

  it("leaves a fence that is no overview a code block", () => {
    expect(readOverviewFence("kind: contacts")).toBe("contacts");
    expect(readOverviewFence("kind: \"issues\" # komentář")).toBe("issues");
    expect(readOverviewFence("kind: Contacts")).toBeUndefined();
    expect(readOverviewFence("kind: contacts\nlevel: s")).toBeUndefined();
    const controller = open([{ type: "paragraph" }]);
    try {
      expect(parseMarkdownContent(editor!, "```a-overview\nkind: Špatně\n```").toJSON().content[0].type).toBe("codeBlock");
    } finally { controller.destroy(); }
  });
});

describe("collect from a page and from a board (one implementation per kind)", () => {
  it("lists a page's issues once each by key, in document order, from their snapshots", () => {
    const page = { type: "doc", content: [{ type: "paragraph", content: [issue("YT-1"), { type: "text", text: " a " }, issue("YT-2", "Fixed")] }, { type: "paragraph", content: [issue("yt-1")] }] };
    const issues = documentIssues(page);
    expect(issues.map(found => `${found.key}/${found.state}`)).toEqual(["YT-1/Open", "YT-2/Fixed"]);
    expect(issueKeys(issues)).toBe("YT-1, YT-2");
    // A Czech Excel splits cells at `;`; the BOM and the formula guard stay.
    expect(issuesCsv([{ key: "YT-3", title: "=SUM(1)", state: "", url: "https://x" }])).toBe("﻿Klíč;Název;Stav;Odkaz\r\n\"YT-3\";\"'=SUM(1)\";\"\";\"https://x\"\r\n");
    // Only a web address becomes a link.
    expect(documentIssues({ type: "doc", content: [{ type: "youtrackIssue", attrs: { key: "YT-4", url: "javascript:alert(1)" } }] })[0]!.url).toBe("");
  });

  it("collects a board's contacts again only for the items that can hold a person", () => {
    const reads = overviewKind("contacts")!.boardReads!;
    expect([{ kind: "note", text: "Porada v úterý" }, { kind: "note", text: "Volat [Jana](a-contact:?objectId=1)" }, { kind: "block" },
      { kind: "object", props: { typeId: "atlas.contact" } }, { kind: "object", props: { typeId: "atlas.place" } }].map(reads)).toEqual([false, true, true, true, false]);
  });

  it("reads a board as one document: its blocks, without the deleted ones", async () => {
    const board = await boardDocument(overviewKind("issues")!, [
      { kind: "block", props: { block: issue("YT-7") } },
      { kind: "block", props: { block: { type: "paragraph", content: [issue("YT-8")] } } },
      { kind: "block", props: { block: issue("YT-9") }, deleted: true },
      { kind: "note", text: "YT-10" },
    ]);
    expect(overviewKind("issues")!.collect(board).map(found => (found as { key: string }).key)).toEqual(["YT-7", "YT-8"]);
  });

  it("counts the requirements a board's product and service cards show, each block of a page once", async () => {
    const block = (id: string, title: string, attrs: Record<string, unknown> = {}) => ({ type: "requirement", attrs: { id, key: id, ...attrs }, content: [{ type: "paragraph", content: [{ type: "text", text: title }] }] });
    const page = { type: "doc", content: [{ type: "heading", attrs: { level: 1, headingId: "portal" }, content: [{ type: "text", text: "Portál" }] },
      block("a", "Server", { state: "done" }), block("b", "SMS brána", { system: "NIA", direction: "inbound" })] };
    const load = vi.fn(async (pageId: string) => (pageId === "p1" ? page : null));
    const nodes = await cardRequirementNodes([
      { kind: "page", targetResourceId: "p1", props: { display: "product", anchor: "portal" } },
      { kind: "page", targetResourceId: "p1", props: { display: "service", anchor: "nia" } },
      { kind: "page", targetResourceId: "p2", props: { display: "product", anchor: "x" } },
    ], load);
    expect(nodes.map(node => node.attrs?.id)).toEqual(["a", "b"]);
    const items = overviewKind("requirements")!.collect({ type: "doc", content: nodes }) as { title: string; attrs: { state: string } }[];
    expect(items.map(item => `${item.title}:${item.attrs.state}`)).toEqual(["Server:done", "SMS brána:planned"]);
    expect(load).toHaveBeenCalledTimes(2);
  });

  // Requirement headings (3.6): a stored page or board is read as the editor reads it, not as 0 requirements (UX review 3.6, F1).
  const text = (value: string) => ({ type: "text", text: value });
  const requirementHeading = (headingId: string, title: string, attrs: Record<string, unknown>) => ({ type: "heading", attrs: { level: 3, headingId, ...attrs }, content: [text(title)] });
  const other = "0b5f3c1e-0000-4000-8000-0000000000b2";

  it("counts a board's requirement headings", async () => {
    const kind = overviewKind("requirements")!;
    const board = await boardDocument(kind, [
      { kind: "block", props: { block: requirementHeading("smlouva", "Smlouva", { state: "done", direction: "inbound", party: "DODAVATEL" }) } },
      { kind: "block", props: { block: requirementHeading("predani", "Předání", { state: "waiting_for_input", direction: "outbound", party: "Nemocnice" }) } },
    ]);
    expect((kind.collect(board) as { title: string; attrs: { state: string; direction: string; party: string } }[])
      .map(item => `${item.title}:${item.attrs.state}:${item.attrs.direction}:${item.attrs.party}`)).toEqual(["Smlouva:done:inbound:DODAVATEL", "Předání:waiting_for_input:outbound:Nemocnice"]);
  });

  it("reads a stored page's requirement headings where no editor holds it (history's rendered changes), another page's dependency as key@pageId", async () => {
    const page = { type: "doc", content: [{ type: "overview", attrs: { kind: "requirements" } },
      requirementHeading("smlouva", "Smlouva", { state: "done" }),
      requirementHeading("predani", "Předání", { state: "planned" }),
      { type: "paragraph", attrs: { dependencies: true }, content: [{ type: "pageReference", attrs: { id: other, title: "VPN", url: `#/pages/${other}?heading=vpn` } }] }] };
    expect((overviewKind("requirements")!.collect(page) as { attrs: { requires: string } }[]).map(item => item.attrs.requires)).toEqual(["", `vpn@${other}`]);
    const card = mount(OverviewReadCard, { props: { kind: "requirements", scope: "", document: page } });
    try {
      await nextTick();
      expect(card.text().replace(/\s+/g, " ")).toContain("1 z 2 hotovo");
    } finally { card.unmount(); }
  });
});

describe("the level: one state per page or board and kind (owner, 2. 11.)", () => {
  const played = { first: vi.fn(), second: vi.fn() };
  const levels = [{ id: "s", label: "S" }, { id: "xl", label: "XL" }];
  registerOverviewKind({ kind: "test-first", label: "První", collect: () => [], card: {}, readCard: {}, levels, animate: (root, change) => { played.first(); change(); } });
  registerOverviewKind({ kind: "test-second", label: "Druhý", collect: () => [], card: {}, readCard: {}, levels, animate: (root, change) => { played.second(); change(); } });
  afterEach(() => { played.first.mockReset(); played.second.mockReset(); });

  it("two overviews of a kind on a page: one dispatch and one motion per change, nothing for the same level, nothing for another kind", async () => {
    const editor = new Editor({ extensions: [Document, Paragraph, Text, OverviewNode.configure({ host: { pageId: () => "page-1" } })],
      content: { type: "doc", content: [{ type: "overview", attrs: { kind: "test-first" } }, { type: "overview", attrs: { kind: "test-first" } }, { type: "overview", attrs: { kind: "test-second" } }] } });
    try {
      const dispatch = vi.spyOn(editor.view, "dispatch");
      expect(editor.view.dom.getAttribute("data-overview-test-first")).toBe("s");
      setOverviewLevel("test-first", "page-1", "xl");
      await nextTick();
      expect([played.first.mock.calls.length, played.second.mock.calls.length, dispatch.mock.calls.length]).toEqual([1, 0, 1]);
      expect(editor.view.dom.getAttribute("data-overview-test-first")).toBe("xl");
      expect(editor.view.dom.getAttribute("data-overview-test-second")).toBe("s");
      setOverviewLevel("test-first", "page-1", "xl");
      await nextTick();
      expect([played.first.mock.calls.length, dispatch.mock.calls.length]).toEqual([1, 1]);
      expect(editor.state.doc.childCount).toBe(3);
      // The page's last overview of a kind gone, its level leaves the root.
      editor.commands.setContent({ type: "doc", content: [{ type: "overview", attrs: { kind: "test-first" } }] });
      expect(editor.view.dom.hasAttribute("data-overview-test-second")).toBe(false);
      expect(editor.view.dom.getAttribute("data-overview-test-first")).toBe("xl");
    } finally { editor.destroy(); }
  });

  // 3.6 perf: the live editor warming up hidden under a read page restyled with it on every switch, and played its motion unseen.
  it("a hidden warming editor takes the level after the read page's switch has painted, without its motion", async () => {
    const host = document.body.appendChild(document.createElement("div"));
    host.className = "editor-hydrating";
    const editor = new Editor({ element: host, extensions: [Document, Paragraph, Text, OverviewNode.configure({ host: { pageId: () => "page-2" } })],
      content: { type: "doc", content: [{ type: "overview", attrs: { kind: "test-first" } }] } });
    try {
      setOverviewLevel("test-first", "page-2", "xl");
      await nextTick();
      expect(played.first).not.toHaveBeenCalled();
      expect(editor.view.dom.getAttribute("data-overview-test-first")).toBe("s");
      await vi.waitFor(() => expect(editor.view.dom.getAttribute("data-overview-test-first")).toBe("xl"));
      expect(played.first).not.toHaveBeenCalled();
    } finally { editor.destroy(); host.remove(); }
  });

  it("walks the page for its kinds only when a transaction touched an overview, not on typing", () => {
    const editor = new Editor({ extensions: [Document, Paragraph, Text, OverviewNode.configure({ host: { pageId: () => "page-1" } })],
      content: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Text" }] }, { type: "overview", attrs: { kind: "test-first" } }] } });
    try {
      const touched = (change: (tr: ReturnType<typeof editor.state.tr.insertText>) => void) => { const tr = editor.state.tr; change(tr); return touchesOverview(tr); };
      expect(touched(tr => tr.insertText("a", 2))).toBe(false);
      expect(touched(tr => tr.insert(0, editor.schema.nodes.overview!.create({ kind: "test-second" })))).toBe(true);
      expect(touched(tr => tr.delete(6, 7))).toBe(true);
      expect(touched(tr => tr.setNodeAttribute(6, "kind", "test-second"))).toBe(true);
      editor.commands.insertContentAt(editor.state.doc.content.size, { type: "overview", attrs: { kind: "test-second" } });
      expect(editor.view.dom.getAttribute("data-overview-test-second")).toBe("s");
    } finally { editor.destroy(); }
  });

  it("a board's root follows the kinds it holds an overview of, one motion per change", async () => {
    const root = document.createElement("div");
    document.body.append(root);
    const shown = new Set(["test-first"]);
    const stop = watchOverviewLevels(root, "board-1", kind => shown.has(kind));
    try {
      expect(root.getAttribute("data-overview-test-first")).toBe("s");
      expect(root.hasAttribute("data-overview-test-second")).toBe(false);
      setOverviewLevel("test-first", "board-1", "xl");
      await nextTick();
      setOverviewLevel("test-first", "board-1", "xl");
      await nextTick();
      expect(root.getAttribute("data-overview-test-first")).toBe("xl");
      expect([played.first.mock.calls.length, played.second.mock.calls.length]).toEqual([1, 0]);
      expect(localStorage.getItem("atlas.overview.test-first.board-1")).toBe("xl");
      stop();
      expect(root.hasAttribute("data-overview-test-first")).toBe(false);
    } finally { stop(); root.remove(); }
  });

  it("a read page with requirements and contacts keeps data-overview-requirements: each watcher touches only its own attributes", () => {
    const root = document.createElement("div");
    // One watcher for the requirements, another (overviewReading.ts) for the page's other overviews, on the same container.
    const requirements = watchOverviewLevels(root, "page-read", kind => kind === "requirements");
    const others = watchOverviewLevels(root, "page-read", kind => kind === "contacts");
    try {
      expect(root.hasAttribute("data-overview-requirements")).toBe(true);
      expect(root.hasAttribute("data-overview-contacts")).toBe(true);
      others();
      expect(root.hasAttribute("data-overview-requirements")).toBe(true);
      expect(root.hasAttribute("data-overview-contacts")).toBe(false);
    } finally { requirements(); others(); }
  });

  it("moves the switch of every overview of the kind when one of them changes it", async () => {
    const people = [{ name: "Jana", emails: ["jana@example.cz"] }];
    const first = mount(PageContactsCard, { props: { contacts: people, pageId: "page-2" }, attachTo: document.body });
    const second = mount(PageContactsCard, { props: { contacts: people, pageId: "page-2" }, attachTo: document.body });
    try {
      await first.findAll(".overview-level-steps > span")[0]!.trigger("click");
      // The pressed switch answers at once; the level (and the page) follows once that frame has painted.
      expect(first.find(".overview-level-steps > [data-on]").text()).toBe("S");
      await vi.waitFor(() => expect(overviewLevel("contacts", "page-2").value).toBe("names"));
      await nextTick();
      expect(second.find("[data-overview-level]").attributes("data-level")).toBe("names");
      expect(second.find(".overview-level-steps > [data-on]").text()).toBe("S");
      expect(second.find("[role=slider]").attributes("aria-valuenow")).toBe("0");
    } finally { first.unmount(); second.unmount(); }
  });
});

describe("AI parity (tests/fixtures/content/overview-mcp.json)", () => {
  it("names an MCP tool for every kind and every action of the overview", () => {
    const parity = fixture("overview-mcp.json") as { kinds: Record<string, string>; actions: Record<string, string> };
    const real = overviewKinds().filter(kind => !kind.kind.startsWith("test-"));
    expect(real.map(kind => kind.kind).filter(kind => !parity.kinds[kind])).toEqual([]);
    expect(real.flatMap(kind => (kind.actions ?? []).map(action => `${kind.kind}.${action.id}`)).filter(action => !parity.actions[action])).toEqual([]);
  });
});
