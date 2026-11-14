import { describe, expect, it, vi } from "vitest";
import type { MarkdownCommand } from "@atlas/markdown/core";
import {
  asBlockNode, asStoredNode, blockDocument, boardBlockExclusions, boardBlockPlugins, defaultBlockNode, firstBlock, inertBlockHost, isKnownNode,
  boardContextBlockHost, boardTextDocument, toBlockModules, useEditorBlockModules,
} from "./editorBlocks";

const command = (id: string) => ({ type: "plugin-command", id }) as unknown as MarkdownCommand;

describe("toBlockModules", () => {
  it("maps slash entries to modules in their order: icon without the registry prefix, keywords as given", () => {
    expect(toBlockModules([
      { id: "contacts.insert", label: "Kontakty", icon: "svg:users", keywords: ["kontakt"], command: command("contacts.insert") },
      { id: "callout", label: "Informační box", description: "Zvýrazněná poznámka", icon: "svg:callout", command: { type: "insert-callout" } },
    ], {})).toEqual([
      { id: "contacts.insert", label: "Kontakty", icon: "users", keywords: ["kontakt"], command: command("contacts.insert") },
      { id: "callout", label: "Informační box", description: "Zvýrazněná poznámka", icon: "callout", keywords: [], command: { type: "insert-callout" } },
    ]);
  });

  it("leaves out excluded entries, entries without an id or a command, and a repeated id", () => {
    const modules = toBlockModules([
      { id: "secret", label: "Heslo", command: command("secret") },
      { label: "Bez id", command: command("x") },
      { id: "no-command", label: "Bez příkazu" },
      { id: "chart", label: "Graf", command: command("chart") },
      { id: "chart", label: "Graf znovu", command: command("chart") },
    ], { secret: "rezervováno" });
    expect(modules.map(module => [module.id, module.label])).toEqual([["chart", "Graf"]]);
  });

  it("gives a module without an icon the editor's generic box", () => {
    expect(toBlockModules([{ id: "x", label: "X", command: command("x") }], {})[0]!.icon).toBe("box");
  });
});

describe("firstBlock", () => {
  const empty = { type: "paragraph", attrs: { textAlign: null } };
  it("returns the first node that is not an empty paragraph", () => {
    const chart = { type: "chart", attrs: { data: { version: 1 } } };
    expect(firstBlock({ type: "doc", content: [empty, chart, empty] })).toEqual(chart);
  });
  it("keeps a paragraph with content (a YouTrack issue is an inline node in one)", () => {
    const issue = { type: "paragraph", content: [{ type: "youtrackIssue", attrs: { key: "ATL-1" } }] };
    expect(firstBlock({ type: "doc", content: [issue, empty] })).toEqual(issue);
  });
  it("is null for a document of empty paragraphs or without content", () => {
    expect(firstBlock({ type: "doc", content: [empty, { type: "paragraph", content: [] }] })).toBeNull();
    expect(firstBlock({ type: "doc" })).toBeNull();
  });
});

it("blockDocument wraps one node into the rich document the renderer and the server's validation take, with the plugin metadata", () => {
  const node = { type: "mathBlock", attrs: { data: { version: 1, expression: "x" } } };
  const value = blockDocument(node);
  expect(value).toMatchObject({ format: "atlas-rich", version: 1, document: { type: "doc", content: [node] } });
  expect(value.plugins).toEqual(expect.arrayContaining([{ id: "math", version: 1 }, { id: "contacts", version: expect.any(Number) }]));
  expect(value.plugins!.map(plugin => plugin.id)).not.toContain("secrets");
});

describe("the board's block modules (the page editor's slash registry)", () => {
  const ids = () => useEditorBlockModules().map(module => module.id);

  it("offers the content plugins and the editor's own blocks a board can hold", () => {
    expect(ids()).toEqual(expect.arrayContaining([
      "contacts.insert", "charts.insert", "diagrams.insert", "math.insert", "youtrack.search", "concealed.toggle",
      "callout", "code-block", "table",
    ]));
  });

  it("leaves out text formats, inline parts and what needs a page, each with its reason", () => {
    for (const id of ["attachments.insert", "openapi.insert", "toc.insert", "contact-inline.insert", "math-inline.insert", "emoji.picker",
      "heading-1", "bullet-list", "task-list", "blockquote", "horizontal-rule", "paste-markdown"]) {
      expect(ids(), id).not.toContain(id);
      expect(boardBlockExclusions[id], id).toMatch(/\S/);
    }
  });

  it("never registers password references on a board (reserved: their protection is bound to a page)", () => {
    expect(boardBlockPlugins(inertBlockHost).map(plugin => plugin.id)).not.toContain("secrets");
    expect(boardBlockPlugins(inertBlockHost)).toBe(boardBlockPlugins(inertBlockHost));
  });

  it("knows the node the slash menu inserts with the plugin's defaults, and none for a picker", () => {
    const modules = useEditorBlockModules();
    const chart = defaultBlockNode(modules.find(module => module.id === "charts.insert")!);
    expect(chart).toMatchObject({ type: "chart", attrs: { data: { version: 1, title: "Nový graf" } } });
    expect(defaultBlockNode(modules.find(module => module.id === "callout")!)).toMatchObject({ type: "callout" });
    expect(defaultBlockNode(modules.find(module => module.id === "youtrack.search")!)).toBeNull();
    expect(document.body.children.length).toBe(0);
  });
});

describe("boardTextDocument", () => {
  // The reader's own plugin metadata, not a second editor's: the same as a block's document carries (the board editor's).
  it("reads a text's Markdown with the board editor's plugin metadata", () => {
    const value = boardTextDocument("**Plán** na pátek");
    expect(value.plugins).toEqual(blockDocument({ type: "paragraph" }).plugins);
    expect(value.plugins?.length).toBeGreaterThan(0);
    expect(JSON.stringify(value.document)).toContain("\"bold\"");
  });
});

describe("asBlockNode", () => {
  it("wraps an inline node of the editor (a YouTrack issue, a mention) into a paragraph", () => {
    const issue = { type: "youtrackIssue", attrs: { id: "2-1", key: "ATL-1", title: "Oprava", url: "https://yt.example/issue/ATL-1", state: null } };
    expect(asBlockNode(issue)).toEqual({ type: "paragraph", content: [issue] });
  });
  it("keeps a block as it is, also one of a plugin it does not know", () => {
    const contact = { type: "contactCard", attrs: { data: { version: 1, contacts: [{ name: "Jana" }] } } };
    expect(asBlockNode(contact)).toBe(contact);
    const unknown = { type: "kanban", attrs: { columns: 3 } };
    expect(asBlockNode(unknown)).toBe(unknown);
  });
});

it("isKnownNode tells a node of the board's editor from one of a plugin it does not have", () => {
  expect(isKnownNode({ type: "contactCard" })).toBe(true);
  expect(isKnownNode({ type: "youtrackIssue" })).toBe(true);
  expect(isKnownNode({ type: "secretReference" })).toBe(false);
  expect(isKnownNode({ type: "kanban" })).toBe(false);
});

describe("asStoredNode", () => {
  it("stores a paragraph that only carries one inline component as that component, as the pickers store it", () => {
    const issue = { type: "youtrackIssue", attrs: { key: "ATL-1" } };
    expect(asStoredNode({ type: "paragraph", attrs: { textAlign: null }, content: [issue] })).toBe(issue);
  });
  it("keeps a paragraph of text, of several parts, and any block", () => {
    const text = { type: "paragraph", content: [{ type: "text", text: "Ahoj" }] };
    expect(asStoredNode(text)).toBe(text);
    const two = { type: "paragraph", content: [{ type: "youtrackIssue", attrs: {} }, { type: "text", text: " a" }] };
    expect(asStoredNode(two)).toBe(two);
    const callout = { type: "callout", content: [{ type: "paragraph" }] };
    expect(asStoredNode(callout)).toBe(callout);
  });
});

describe("boardContextBlockHost", () => {
  it("asks the YouTrack endpoints of the board itself, which map its space to projects as a page of the space does", async () => {
    const fetch = vi.spyOn(globalThis, "fetch").mockImplementation(async () => new Response("[]", { status: 200, headers: { "Content-Type": "application/json" } }));
    try {
      await boardContextBlockHost("board 1").youTrack.search("oprava", { signal: new AbortController().signal });
      expect(String(fetch.mock.calls[0]![0])).toContain("/api/boards/board%201/youtrack/search?q=oprava");
    } finally { fetch.mockRestore(); }
  });

  it("answers null for an issue the board cannot see, and lets any other failure through", async () => {
    const fetch = vi.spyOn(globalThis, "fetch").mockImplementation(async () => new Response("{}", { status: 404, headers: { "Content-Type": "application/json" } }));
    try {
      expect(await boardContextBlockHost("b1").youTrack.resolve!("ATL-1", { signal: new AbortController().signal })).toBeNull();
      expect(String(fetch.mock.calls[0]![0])).toContain("/api/boards/b1/youtrack/resolve?reference=ATL-1");
    } finally { fetch.mockRestore(); }
  });
});
