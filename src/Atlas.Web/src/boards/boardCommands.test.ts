// @vitest-environment node
import { iconMarkup } from "@atlas/markdown/icons";
import { describe, expect, it } from "vitest";
import { actionCommands, boardCommands, findCommands, itemCommands, type CommandContext } from "./boardCommands";
import type { BoardItem } from "./boardModel";
import type { EditorBlockModule } from "./editorBlocks";

const item = (id: string, kind: BoardItem["kind"], extra: Partial<BoardItem> = {}): BoardItem => ({ id, kind, x: 0, y: 0, width: 100, height: 100, z: 1, ...extra });
const module = (id: string, label: string, keywords: string[] = []): EditorBlockModule => ({ id, label, icon: "callout", keywords, command: { type: id } as never });
const modules = [module("callout", "Zvýraznění", ["poznámka", "upozornění"]), module("youtrack.search", "YouTrack"), module("contacts.insert", "Kontakty"), module("charts.insert", "Graf")];
const context = (extra: Partial<CommandContext> = {}): CommandContext =>
  ({ canEdit: true, selection: [], modules, can: { pickPage: true, pickIssues: true, pickContacts: true, createPage: true }, ...extra });
const ids = (extra: Partial<CommandContext> = {}) => boardCommands(context(extra)).map(command => command.id);

describe("the board's command panel (objects spec §5.2)", () => {
  it("offers creating, the editor's components, placing a page and the board's settings over empty board", () => {
    expect(ids()).toEqual([
      "create:note", "create:text", "create:shape", "create:frame",
      "pick:youtrack", "pick:contact", "component:callout", "component:charts.insert",
      "place:page", "board:settings", "board:detail",
    ]);
  });

  it("puts the selection's actions first, only those that fit it", () => {
    const notes = ids({ selection: [item("a", "note"), item("b", "note")] });
    expect(notes.filter(id => id.startsWith("selection:"))).toEqual([
      "selection:group", "selection:align-left", "selection:align-center", "selection:align-right", "selection:align-top", "selection:align-middle",
      "selection:align-bottom", "selection:duplicate", "selection:delete",
    ]);
    expect(notes[0]).toBe("selection:group");
    // Grouping needs two items; a group alone can be dissolved or collapsed.
    expect(ids({ selection: [item("g", "group")] }).filter(id => id.startsWith("selection:"))).toEqual(["selection:ungroup", "selection:collapse", "selection:duplicate", "selection:delete"]);
    expect(ids({ selection: [item("g", "group", { props: { collapsed: true } })] })).toContain("selection:expand");
    expect(ids({ selection: [item("p", "page", { targetResourceId: "page-1" })] })).toContain("selection:detail");
    expect(ids({ selection: [item("n", "note", { text: "Plán" })] })).toContain("selection:to-page");
    expect(ids({ selection: [item("n", "note")], can: { pickPage: false, pickIssues: false, pickContacts: false, createPage: false } })).not.toContain("selection:to-page");
  });

  it("offers „Uspořádat jako architekturu“ where the host lays out cards: over the whole board, or over the selection", () => {
    const can = { pickPage: true, pickIssues: true, pickContacts: true, createPage: true, arrange: true };
    expect(ids()).not.toContain("board:arrange");
    const whole = boardCommands(context({ can })).find(command => command.id === "board:arrange")!;
    expect([whole.section, whole.title]).toEqual(["board", "Uspořádat jako architekturu"]);
    const chosen = boardCommands(context({ can, selection: [item("p", "page")] })).filter(command => command.id === "board:arrange");
    expect(chosen.map(command => [command.section, command.title])).toEqual([["selection", "Uspořádat výběr jako architekturu"]]);
    // A reader arranges nothing; „uspořádat“ finds it.
    expect(ids({ can, canEdit: false })).not.toContain("board:arrange");
    expect(findCommands("uspořádat", boardCommands(context({ can })))[0]!.id).toBe("board:arrange");
  });

  it("offers the lists derived from the relations as components where the board has a space, „Na co čekáme“ only with Projector (P15)", () => {
    const can = { pickPage: true, pickIssues: true, pickContacts: true, createPage: true };
    expect(ids().filter(id => id.startsWith("links:"))).toEqual([]);
    expect(ids({ can: { ...can, links: true } }).filter(id => id.startsWith("links:"))).toEqual(["links:people", "links:usage"]);
    expect(ids({ can: { ...can, links: true, waiting: true } }).filter(id => id.startsWith("links:"))).toEqual(["links:people", "links:waiting", "links:usage"]);
    expect(boardCommands(context({ can: { ...can, links: true, waiting: true } })).filter(command => command.id.startsWith("links:")).every(command => command.section === "component")).toBe(true);
    expect(ids({ canEdit: false, can: { ...can, links: true, waiting: true } }).filter(id => id.startsWith("links:"))).toEqual([]);
  });

  it("leaves a reader only what changes nothing", () => {
    expect(ids({ canEdit: false, selection: [item("p", "page", { targetResourceId: "page-1" }), item("n", "note")] })).toEqual(["board:settings", "board:detail"]);
    expect(ids({ canEdit: false, selection: [item("p", "page", { targetResourceId: "page-1" })] })).toEqual(["selection:detail", "board:settings", "board:detail"]);
  });

  it("2B: a contact card offers its detail, business card and QR code as rows, to a reader too; a board's card no detail", () => {
    const contact = item("c", "object", { targetResourceId: "contact-1", props: { typeId: "atlas.contact", objectId: "contact-1" } });
    const rows = (canEdit: boolean, chosen = contact) => actionCommands(context({ canEdit, selection: [chosen] }), {}).map(command => command.id);
    expect(rows(false)).toEqual(["selection:detail", "selection:comments", "selection:vcard", "selection:qr"]);
    expect(rows(true).slice(0, 4)).toEqual(["selection:detail", "selection:comments", "selection:vcard", "selection:qr"]);
    // Nothing else opens it: its detail takes Ctrl+Enter.
    expect(actionCommands(context({ selection: [contact] }), {})[0]!.description).toBe("Ctrl+Enter");
    expect(ids({ canEdit: false, selection: [contact] })).toEqual(["selection:detail", "board:settings", "board:detail"]);
    expect(rows(false, item("b", "object", { targetResourceId: "board-1", props: { typeId: "atlas.board" } }))).toEqual(["selection:comments"]);
  });

  it("D13: one selected item offers its Diskuse, to a reader too; a connector or a selection of several does not", () => {
    const rows = (selection: BoardItem[], canEdit = false) => actionCommands(context({ canEdit, selection }), {}).map(command => command.id);
    expect(rows([item("n", "note")])).toEqual(["selection:comments"]);
    expect(rows([item("p", "page", { targetResourceId: "page-1" })])).toEqual(["selection:detail", "selection:comments"]);
    expect(rows([item("n", "note")], true)).toContain("selection:comments");
    expect(rows([item("c", "connector", { from: "a", to: "b" })])).toEqual([]);
    expect(rows([item("a", "note"), item("b", "note")])).toEqual([]);
    expect(actionCommands(context({ selection: [item("n", "note")] }), {}).find(command => command.id === "selection:comments")!.title).toBe("Diskuse");
  });

  it("finds a command by its title or keywords, without regard to case and accents, the title's start first", () => {
    const all = boardCommands(context());
    expect(findCommands("pozn", all).map(command => command.id).slice(0, 2)).toEqual(["create:note", "component:callout"]);
    expect(findCommands("RAM", all)[0]?.id).toBe("create:frame");
    expect(findCommands("seskupit", boardCommands(context({ selection: [item("a", "note"), item("b", "note")] })))[0]?.id).toBe("selection:group");
    expect(findCommands("", all)).toEqual(all);
    expect(findCommands("xyz", all)).toEqual([]);
  });

  it("puts the exact title first, whatever the case and diacritics, before titles that only start with it", () => {
    const listed = boardCommands(context({ modules: [module("charts.many", "Grafy"), module("charts.insert", "Graf")] }));
    expect(findCommands("graf", listed)[0]?.id).toBe("component:charts.insert");
    expect(findCommands("GRAF", listed)[0]?.id).toBe("component:charts.insert");
    expect(findCommands("poznamka", listed)[0]?.id).toBe("create:note");
  });

  it("draws every entry with an icon of the editor's registry, as the slash menu does", () => {
    const every = boardCommands(context({ selection: [item("g", "group"), item("n", "note"), item("p", "page")], can: { pickPage: true, pickIssues: true, pickContacts: true, createPage: true, links: true, waiting: true } }));
    for (const command of [...every, ...boardCommands(context({ selection: [item("p", "page", { targetResourceId: "x" })] }))]) expect(Object.hasOwn(iconMarkup, command.icon), command.id).toBe(true);
  });
});

describe("finding an item by name (BD2-7)", () => {
  const label = (found: BoardItem) => (found.kind === "object" ? undefined : `${found.kind}: ${found.text ?? ""}`);
  it("makes an entry of each item with a name, a connector too, none for deleted items or nameless ones", () => {
    const entries = itemCommands([item("a", "note", { text: "Ansible" }), item("c", "connector", { text: "nasazuje" }), item("d", "note", { deleted: true, text: "x" }), item("o", "object")], label);
    expect(entries.map(entry => [entry.id, entry.title, entry.section])).toEqual([["find:a", "note: Ansible", "find"], ["find:c", "connector: nasazuje", "find"]]);
  });
  it("is ranked as the commands are: the start of the name first", () => {
    const entries = itemCommands([item("a", "note", { text: "Nasazení Ansible" }), item("b", "note", { text: "Ansible" })], found => found.text);
    expect(findCommands("ansi", entries).map(entry => entry.id)).toEqual(["find:b", "find:a"]);
  });
});
