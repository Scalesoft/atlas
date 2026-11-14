// @vitest-environment node
import { afterEach, describe, expect, it, vi } from "vitest";
import { contactNode, normalizeContact } from "@atlas/markdown/plugins/contacts";
import { ApiError } from "../api";
import { contactBlock, resolvePasteItems, youtrackNode, youtrackResolver, type YouTrackIssueSnapshot } from "./boardBlockNodes";
import type { PasteItem } from "./boardPaste";

const get = vi.fn();
vi.mock("../api", async importOriginal => ({ ...(await importOriginal<typeof import("../api")>()), api: (path: string, init?: RequestInit) => get(path, init) }));
afterEach(() => get.mockReset());

const issue: YouTrackIssueSnapshot = { id: "2-12", key: "ATL-12", title: "Opravit přihlášení", url: "https://yt.example/issue/ATL-12", state: "Open" };

describe("youtrackNode", () => {
  it("is the node the editor's youtrack plugin stores: its attributes, state null when there is none", () => {
    expect(youtrackNode(issue)).toEqual({ type: "youtrackIssue", attrs: { id: "2-12", key: "ATL-12", title: "Opravit přihlášení", url: "https://yt.example/issue/ATL-12", state: "Open" } });
    expect(youtrackNode({ ...issue, state: undefined })?.attrs?.state).toBeNull();
    expect(youtrackNode({ ...issue, state: null })?.attrs?.state).toBeNull();
  });

  it("trims, and writes the URL as the editor does (href)", () => {
    const node = youtrackNode({ id: " 2-12 ", key: " ATL-12", title: " Název ", url: " https://yt.example ", state: " Open " });
    expect(node?.attrs).toEqual({ id: "2-12", key: "ATL-12", title: "Název", url: "https://yt.example/", state: "Open" });
  });

  it.each([
    ["no id", { id: " " }],
    ["no key", { key: "" }],
    ["no title", { title: "" }],
    ["a title of several lines", { title: "a\nb" }],
    ["a title that is too long", { title: "x".repeat(2001) }],
    ["a javascript URL", { url: "javascript:alert(1)" }],
    ["a URL with credentials", { url: "https://u:p@yt.example/issue/ATL-12" }],
    ["a relative URL", { url: "/issue/ATL-12" }],
    ["a blank state", { state: " " }],
    ["a state with a control character", { state: "Op\u0007en" }],
  ])("refuses an issue with %s, as the editor does", (_, change) => {
    expect(youtrackNode({ ...issue, ...change })).toBeNull();
  });
});

describe("contactBlock", () => {
  const contact = { name: "Jana Nováková", title: "Vedoucí", emails: ["jana@example.cz"], phones: ["+420 777 123 456"] };

  it("is the contacts block of the editor: the node its plugin makes, of the contact as its rules normalize it", async () => {
    const block = await contactBlock(contact);
    expect(block).toEqual(contactNode(normalizeContact(contact)));
    expect(block).toEqual({ type: "contactCard", attrs: { data: { version: 1, contacts: [contact] } } });
  });

  it("refuses what the editor refuses, with its message", async () => {
    await expect(contactBlock({ name: "  " })).rejects.toThrow("Vyplňte pole: jméno.");
    await expect(contactBlock({ name: "A", emails: ["not an e-mail"] })).rejects.toThrow("Zkontrolujte e-mailové adresy.");
  });
});

describe("resolvePasteItems", () => {
  const page: PasteItem = { kind: "page", resourceId: "3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b", anchor: "uvod" };
  const note: PasteItem = { kind: "note", text: "Poznámka" };

  it("makes the youtrackIssue block of each reference that resolves and keeps the order, pages and notes as they are", async () => {
    const resolve = vi.fn(async (id: string) => ({ ...issue, key: id }));
    const result = await resolvePasteItems([page, { kind: "youtrack", issueId: "ATL-1" }, note, { kind: "youtrack", issueId: "ATL-2" }], resolve);
    expect(result).toEqual([
      page,
      { kind: "block", block: youtrackNode({ ...issue, key: "ATL-1" }) },
      note,
      { kind: "block", block: youtrackNode({ ...issue, key: "ATL-2" }) },
    ]);
  });

  it("keeps a reference that does not resolve, is refused or fails as the note it was written as", async () => {
    const answers: Record<string, () => Promise<YouTrackIssueSnapshot | null>> = {
      "NONE-1": async () => null,
      "BAD-2": async () => ({ ...issue, url: "javascript:alert(1)" }),
      "DOWN-3": async () => { throw new Error("503"); },
    };
    const result = await resolvePasteItems(Object.keys(answers).map(issueId => ({ kind: "youtrack" as const, issueId })), id => answers[id]!());
    expect(result).toEqual([{ kind: "note", text: "NONE-1" }, { kind: "note", text: "BAD-2" }, { kind: "note", text: "DOWN-3" }]);
  });

  it("asks for every reference at once, so the board's resolver sends them in one request, and nothing for a paste without references", async () => {
    const resolve = vi.fn(async (id: string) => ({ ...issue, key: id }));
    const items = Array.from({ length: 20 }, (_, index) => ({ kind: "youtrack" as const, issueId: `ATL-${index + 1}` }));
    const pending = resolvePasteItems(items, resolve);
    expect(resolve).toHaveBeenCalledTimes(20);
    expect(await pending).toHaveLength(20);
    expect(await resolvePasteItems([], resolve)).toEqual([]);
    expect(await resolvePasteItems([note], async () => { throw new Error("never asked"); })).toEqual([note]);
  });
});

describe("youtrackResolver", () => {
  it("asks the board's own resolve (its space's projects) once for all the references asked together, each answered in its order", async () => {
    get.mockResolvedValue([issue, null]);
    const resolve = youtrackResolver("b 1");
    expect(await Promise.all([resolve("ATL-12"), resolve("NONE-1")])).toEqual([issue, null]);
    expect(get).toHaveBeenCalledTimes(1);
    expect(get).toHaveBeenCalledWith("/boards/b%201/youtrack/resolve", { method: "POST", body: JSON.stringify({ references: ["ATL-12", "NONE-1"] }) });
    get.mockResolvedValue([issue]);
    expect(await resolve("ATL-12")).toEqual(issue);
    expect(get).toHaveBeenCalledTimes(2);
  });

  it("splits more than the server takes at once into requests of 100", async () => {
    get.mockImplementation(async (_path: string, init: RequestInit) => (JSON.parse(String(init.body)) as { references: string[] }).references.map(key => ({ ...issue, key })));
    const resolve = youtrackResolver("b1");
    const keys = Array.from({ length: 150 }, (_, index) => `ATL-${index + 1}`);
    expect((await Promise.all(keys.map(resolve))).map(found => found?.key)).toEqual(keys);
    expect(get).toHaveBeenCalledTimes(2);
  });

  it("lets a failure of the request through to every reference in it", async () => {
    get.mockRejectedValueOnce(new ApiError(503, "nedostupný"));
    await expect(youtrackResolver("s1")("ATL-12")).rejects.toMatchObject({ status: 503 });
  });
});
