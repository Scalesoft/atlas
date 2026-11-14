import type { Editor } from "@tiptap/core";
import { afterEach, describe, expect, it, vi } from "vitest";
import { boardApi, type BoardDto } from "../boards/boardApi";
import { organizationApi } from "../organization";
import { toast } from "vue-sonner";
import { composeBoardFrom, composeDeploymentBoard, openDeploymentBoard, pageBoardId } from "./composeDeploymentBoard";

const document = { type: "doc", content: [
  { type: "heading", attrs: { level: 2, headingId: "portal" }, content: [{ type: "text", text: "Portál" }] },
  { type: "requirement", attrs: { id: "b1", direction: "inbound", system: "NIA" }, content: [{ type: "paragraph", content: [{ type: "text", text: "Certifikát" }] }] },
] };
const editor = { isEditable: true, getJSON: () => document, storage: { requirementRuntime: { host: { pageId: () => "page-1", spaceId: () => "space-1" } } } } as unknown as Editor;
const board = (id: string, items: BoardDto["items"] = []): BoardDto => ({ id, spaceId: "space-1", title: "Tabule", sequence: 1, canEdit: true, items });
const tree = (ids: string[], attachedBoardId?: string) => ({ spaceId: "space-1", version: 1, items: [
  { id: "page-1", parentId: null, title: "Zakázka", version: 1, sortOrder: 0, isPublished: true, canEdit: true, attachedBoardId },
  ...ids.map(id => ({ id, parentId: "page-1", title: "Tabule", version: 1, sortOrder: 1, isPublished: true, canEdit: true, type: "board" as const }))] });
const source = { pageId: "page-1", spaceId: "space-1", document };

afterEach(() => { vi.restoreAllMocks(); location.hash = ""; });

describe("composeBoardFrom", () => {
  it("makes the page's own board when none of its boards shows the deployment, fills it and opens it", async () => {
    vi.spyOn(organizationApi, "tree").mockResolvedValue(tree(["other"]));
    vi.spyOn(boardApi, "get").mockResolvedValue(board("other"));
    const create = vi.spyOn(boardApi, "create").mockResolvedValue(board("new"));
    const submit = vi.spyOn(boardApi, "submit").mockResolvedValue({ sequence: 2, duplicate: false });
    await composeBoardFrom(source);
    expect(create).toHaveBeenCalledWith("space-1", "Tabule nasazení", "page-1", "projector.board");
    expect(submit.mock.calls[0]![0]).toBe("new");
    expect(submit.mock.calls[0]![2].map(change => change.type === "create" && change.item.kind)).toEqual(["page", "page", "connector"]);
    expect(location.hash).toContain("new");
  });

  it("fills the 3.5.0 board under the page that already shows its deployment, and sends nothing when it lacks nothing", async () => {
    const card = (id: string, display: "product" | "service", anchor: string) =>
      ({ id, kind: "page" as const, x: 0, y: 0, width: 300, height: 150, z: 1, version: 1, props: { display, anchor }, target: { resourceId: "page-1", type: "atlas.page", title: "Zakázka", available: true } });
    const items = [card("p", "product", "portal"), card("s", "service", "nia"), { id: "c", kind: "connector" as const, x: 0, y: 0, width: 8, height: 8, z: 0, version: 1, from: "p", to: "s" }];
    vi.spyOn(organizationApi, "tree").mockResolvedValue(tree(["mine"]));
    vi.spyOn(boardApi, "get").mockResolvedValue(board("mine", items));
    const create = vi.spyOn(boardApi, "create");
    const submit = vi.spyOn(boardApi, "submit");
    await composeBoardFrom(source);
    expect(create).not.toHaveBeenCalled();
    expect(submit).not.toHaveBeenCalled();
    expect(location.hash).toContain("mine");
  });

  it("fills the page's own board, found by the page's attachedBoardId, and never a tree board beside it", async () => {
    vi.spyOn(organizationApi, "tree").mockResolvedValue(tree(["left"], "own"));
    const get = vi.spyOn(boardApi, "get").mockResolvedValue(board("own"));
    const create = vi.spyOn(boardApi, "create");
    const submit = vi.spyOn(boardApi, "submit").mockResolvedValue({ sequence: 2, duplicate: false });
    await composeBoardFrom(source);
    expect(get.mock.calls).toEqual([["own"]]);
    expect(create).not.toHaveBeenCalled();
    expect(submit.mock.calls[0]![0]).toBe("own");
  });

  it("fills the empty board of its title a failed run left under the page instead of making another", async () => {
    vi.spyOn(organizationApi, "tree").mockResolvedValue(tree(["left"]));
    vi.spyOn(boardApi, "get").mockResolvedValue({ ...board("left"), title: "Tabule nasazení" });
    const create = vi.spyOn(boardApi, "create");
    const submit = vi.spyOn(boardApi, "submit").mockResolvedValue({ sequence: 2, duplicate: false });
    await composeBoardFrom(source);
    expect(create).not.toHaveBeenCalled();
    expect(submit.mock.calls[0]![0]).toBe("left");
  });
});

describe("openDeploymentBoard", () => {
  const made = [{ id: "p", kind: "page" as const, x: 0, y: 0, width: 300, height: 150, z: 1, version: 1, props: { display: "product" as const, anchor: "portal" }, target: { resourceId: "page-1", type: "atlas.page", title: "Zakázka", available: true } }];

  it("opens the board the page already has without touching it, for a reader too", async () => {
    vi.spyOn(organizationApi, "tree").mockResolvedValue(tree([], "own"));
    vi.spyOn(boardApi, "get").mockResolvedValue(board("own", made));
    const submit = vi.spyOn(boardApi, "submit");
    await openDeploymentBoard(source, false);
    expect(location.hash).toContain("own");
    expect(submit).not.toHaveBeenCalled();
  });

  it("tops up the page's board for who may edit the page: a new product gets its card, and no second board is made", async () => {
    const grown = { ...document, content: [...document.content,
      { type: "heading", attrs: { level: 2, headingId: "registr" }, content: [{ type: "text", text: "Registr" }] },
      { type: "requirement", attrs: { id: "b2" }, content: [{ type: "paragraph", content: [{ type: "text", text: "Databáze" }] }] }] };
    vi.spyOn(organizationApi, "tree").mockResolvedValue(tree([], "own"));
    vi.spyOn(boardApi, "get").mockResolvedValue(board("own", made));
    const create = vi.spyOn(boardApi, "create");
    const submit = vi.spyOn(boardApi, "submit").mockResolvedValue({ sequence: 2, duplicate: false });
    await openDeploymentBoard({ ...source, document: grown }, true);
    expect(create).not.toHaveBeenCalled();
    expect(submit.mock.calls[0]![0]).toBe("own");
    const added = submit.mock.calls.flatMap(call => call[2]).flatMap(change => (change.type === "create" ? [change.item] : []));
    expect(added.filter(item => item.kind === "page").map(item => item.props?.anchor)).toContain("registr");
    expect(added.some(item => item.kind === "page" && item.props?.anchor === "portal")).toBe(false);
    expect(location.hash).toContain("own");
  });

  it("is what the summary's „Tabule nasazení“ runs in the editor: it composes, then opens", async () => {
    vi.spyOn(organizationApi, "tree").mockResolvedValue(tree([], "own"));
    vi.spyOn(boardApi, "get").mockResolvedValue(board("own", made));
    const submit = vi.spyOn(boardApi, "submit").mockResolvedValue({ sequence: 2, duplicate: false });
    await composeDeploymentBoard(editor);
    expect(submit.mock.calls.every(call => call[0] === "own")).toBe(true);
    expect(location.hash).toContain("own");
  });

  it("composes it for a writer, and only says so to a reader, who cannot make it", async () => {
    vi.spyOn(organizationApi, "tree").mockResolvedValue(tree([]));
    const create = vi.spyOn(boardApi, "create").mockResolvedValue(board("new"));
    vi.spyOn(boardApi, "submit").mockResolvedValue({ sequence: 2, duplicate: false });
    const warning = vi.spyOn(toast, "warning");
    await openDeploymentBoard(source, false);
    expect(create).not.toHaveBeenCalled();
    expect(warning).toHaveBeenCalled();
    await openDeploymentBoard(source, true);
    expect(create).toHaveBeenCalledOnce();
    expect(location.hash).toContain("new");
  });
});

describe("pageBoardId", () => {
  // 3.6 perf: the reader's view and the warmed editor each read the whole tree to find one board, besides the shell's own read.
  it("finds the page's board in the tree read moments ago (the shell's or its own), not by reading the tree again", async () => {
    const read = vi.spyOn(organizationApi, "tree").mockResolvedValue({ ...tree([], "own"), spaceId: "space-recent" });
    vi.spyOn(boardApi, "get").mockResolvedValue(board("own", [{ id: "p", kind: "page", x: 0, y: 0, width: 300, height: 150, z: 1, version: 1,
      props: { display: "product", anchor: "portal" }, target: { resourceId: "page-1", type: "atlas.page", title: "Zakázka", available: true } }]));
    expect(await pageBoardId("space-recent", "page-1")).toBe("own");
    expect(await pageBoardId("space-recent", "page-1")).toBe("own");
    expect(read).toHaveBeenCalledTimes(1);
  });
});