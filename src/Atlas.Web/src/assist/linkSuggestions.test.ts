import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import * as assistEntry from "@atlas/markdown/assist";
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin } from "@atlas/markdown/core";
import { insertPageReference } from "@atlas/markdown/plugins/pages";
import type { Editor, JSONContent } from "@tiptap/core";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { api, ApiError } from "../api";
import { createDocumentWidgets } from "../documentWidgets";
import { createAtlasAssist } from "./atlasAssist";
import { createLinkCandidateClient, type LinkCandidate } from "./linkSuggestions";
import type { AtlasAssistContext } from "./types";

vi.mock("../api", async importOriginal => ({ ...await importOriginal<typeof import("../api")>(), api: vi.fn() }));
const call = vi.mocked(api);

// Plan §19 E2: a mention of a page in a finished paragraph is offered as a link (icon `link`, never the AI glyph) with
// two actions: „Ponechat text jako odkaz“ (default) and „Nahradit názvem stránky“, each one undo step.

const vpnPage = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
const onboardingPage = "3f2504e0-4f89-41d3-9a0c-0305e82c3302";
const backupPage = "3f2504e0-4f89-41d3-9a0c-0305e82c3303";
const currentPage = "3f2504e0-4f89-41d3-9a0c-0305e82c3300";
const candidate = (phrase: string, pageId: string, title: string, matchedBy: LinkCandidate["matchedBy"] = "title"): LinkCandidate =>
  ({ phrase, pageId, spaceId: "it", spaceName: "IT a infrastruktura", title, matchedBy });

const t = (text: string, marks?: { type: string; attrs?: Record<string, unknown> }[]): JSONContent => ({ type: "text", text, ...(marks ? { marks } : {}) });
const p = (...parts: (string | JSONContent)[]): JSONContent => {
  const content = parts.filter(part => part !== "").map(part => typeof part === "string" ? t(part) : part);
  return content.length ? { type: "paragraph", content } : { type: "paragraph" };
};

function fakeContext(overrides: Partial<AtlasAssistContext> = {}) {
  return {
    pageId: () => currentPage,
    contacts: { search: vi.fn(async () => []), get: vi.fn(async () => null) },
    pages: { search: vi.fn(async () => []), resolve: vi.fn(async () => null) },
    resolveIssue: vi.fn(async () => null),
    hasSecrets: vi.fn(async () => false),
    pickSecret: vi.fn(async () => null),
    notify: vi.fn(),
    warn: vi.fn(),
    linkCandidates: vi.fn(async (): Promise<LinkCandidate[]> => []),
    ...overrides,
  } satisfies AtlasAssistContext;
}

const controllers: MarkdownEditorController[] = [];
beforeEach(() => { call.mockReset(); });
afterEach(() => {
  controllers.splice(0).forEach(controller => controller.destroy());
  document.body.replaceChildren();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

function mount(content: JSONContent[], assistPlugin: MarkdownPlugin) {
  let editor!: Editor;
  const capture: MarkdownPlugin = { id: "capture", apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor; } }) };
  const controller = createMarkdownEditorController({
    profile: "rich",
    plugins: [...createDocumentWidgets({ kind: "edit" }), assistPlugin, capture],
    source: { kind: "local", value: { format: "atlas-rich", version: 1, document: { type: "doc", content } } },
  });
  controllers.push(controller);
  const host = document.createElement("div");
  document.body.append(host);
  controller.attachView(host, { ariaLabel: "Dokument" });
  return { editor, controller, host };
}

interface Suggestion {
  id: string; kind: string; title: string; description?: string; icon: string; ai?: boolean; range?: { from: number; to: number };
  apply(context: unknown): boolean | Promise<boolean>;
}
interface Source { id: string; suggest(block: unknown, context: { signal: AbortSignal }): Promise<Suggestion[]> }

/** The module's `createAssistPlugin` replaced by a recorder, so the link source can be called directly. */
function withDouble(content: JSONContent[], overrides: Partial<AtlasAssistContext> = {}) {
  const state: { sources?: Source[] } = {};
  const module = {
    createAssistPlugin: (options: { sources: Source[] }) => { state.sources = options.sources; return { id: "assist", apiVersion: 1 as const, create: () => ({}) }; },
    openMarkdownProposalPanel: vi.fn(),
    defaultAssistStrings: {},
  };
  const context = fakeContext(overrides);
  const mounted = mount(content, createAtlasAssist(module as never, context));
  const source = state.sources!.find(item => item.id === "atlas-links")!;
  const suggest = (match: string, trigger = "leave") => source.suggest(blockAt(mounted.editor, match, trigger), { signal: new AbortController().signal });
  return { ...mounted, context, suggest };
}

function blockAt(editor: Editor, match: string, trigger = "leave") {
  let pos = -1;
  let node: ProseMirrorNode | undefined;
  editor.state.doc.descendants((child, at) => {
    if (pos < 0 && child.isTextblock && child.textContent.includes(match)) { pos = at; node = child; }
    return pos < 0;
  });
  let text = "";
  node!.forEach(child => { text += child.isText ? child.text! : child.type.name === "hardBreak" ? "\n" : "￼"; });
  return { pos, node: node!, text, trigger, rangeOf: (start: number, end: number) => ({ from: pos + 1 + start, to: pos + 1 + end }) };
}

function linkHrefs(editor: Editor) {
  const found: [string, string][] = [];
  editor.state.doc.descendants(node => {
    const link = node.marks.find(mark => mark.type.name === "link");
    if (node.isText && link) found.push([node.text!, String(link.attrs.href)]);
    return true;
  });
  return found;
}
const types = (editor: Editor) => {
  const found: string[] = [];
  editor.state.doc.descendants(node => { found.push(node.type.name); return true; });
  return found;
};

describe("link mentions with the editor's assist module", () => {
  beforeEach(() => { vi.useFakeTimers(); });
  const settle = () => vi.advanceTimersByTimeAsync(0);
  const key = (editor: Editor, name: string, init: KeyboardEventInit = {}) =>
    editor.view.dom.dispatchEvent(new KeyboardEvent("keydown", { key: name, bubbles: true, cancelable: true, ...init }));
  const markers = () => [...document.querySelectorAll<HTMLButtonElement>(".md-assist-marker:not([data-leaving])")];
  // Alt+Enter: the editor's „Co s tím jde dělat“ with the block's suggestions first.
  const rows = () => [...document.querySelectorAll<HTMLElement>("[data-testid=intentions] [role=option]")].map(row => row.textContent ?? "");

  async function finishParagraph() {
    const linkCandidates = vi.fn(async (_request: unknown, _signal: AbortSignal) => [candidate("VPN", vpnPage, "Připojení k VPN")]);
    const context = fakeContext({ linkCandidates });
    const mounted = mount([p("")], createAtlasAssist(assistEntry as never, context));
    mounted.editor.view.focus();
    mounted.editor.commands.insertContent("Před vzdálenou správou si nastavte VPN podle návodu.");
    // Enter: the caret leaves the finished paragraph, which is analysed once.
    mounted.editor.commands.splitBlock();
    await settle();
    return { ...mounted, context, linkCandidates };
  }

  it("offers a link mark by default and a page reference on request as one undo step", async () => {
    const { editor, controller, context, linkCandidates } = await finishParagraph();
    expect(linkCandidates).toHaveBeenCalledTimes(1);
    expect(linkCandidates.mock.calls[0]![0]).toMatchObject({ pageId: currentPage, phrases: expect.arrayContaining(["VPN"]) });
    expect(markers()).toHaveLength(1);
    expect(markers()[0]!.getAttribute("aria-label")).toBe("Návrh: Propojit „VPN“ se stránkou „Připojení k VPN“");
    key(editor, "Enter", { altKey: true });
    await settle();
    expect(rows()).toHaveLength(2);
    expect(rows()[0]).toContain("Ponechat text jako odkaz");
    expect(rows()[1]).toContain("Nahradit názvem stránky");
    expect(document.querySelector(".md-assist-ai-tag, [data-ai-glyph]")).toBeNull();

    // Enter takes the default: the words stay, they link to the page.
    key(editor, "Enter");
    await settle();
    expect(editor.state.doc.child(0).textContent).toBe("Před vzdálenou správou si nastavte VPN podle návodu.");
    expect(linkHrefs(editor)).toEqual([["VPN", `#/pages/${vpnPage}`]]);
    expect(context.notify).toHaveBeenCalledWith("Text propojen se stránkou", expect.any(Function));
    // The author keeps typing in the next paragraph.
    editor.commands.insertContent("Další věta.");
    expect(editor.state.doc.child(1).textContent).toBe("Další věta.");
    controller.dispatch({ type: "undo" });
    expect(editor.state.doc.child(1).textContent).toBe("");
    controller.dispatch({ type: "undo" });
    expect(linkHrefs(editor)).toEqual([]);
    expect(editor.state.doc.child(0).textContent).toBe("Před vzdálenou správou si nastavte VPN podle návodu.");
  });

  it("replaces the words with the page's name on request, one undo step", async () => {
    const { editor, controller } = await finishParagraph();
    key(editor, "Enter", { altKey: true });
    await settle();
    key(editor, "ArrowDown");
    key(editor, "Enter");
    await settle();
    expect(types(editor)).toContain("pageReference");
    expect(editor.state.doc.child(0).textContent).not.toContain("VPN");
    controller.dispatch({ type: "undo" });
    expect(types(editor)).not.toContain("pageReference");
    expect(editor.state.doc.child(0).textContent).toBe("Před vzdálenou správou si nastavte VPN podle návodu.");
  });
});

describe("the section around the caret for [[ ordering (E4)", () => {
  const h2 = (text: string): JSONContent => ({ type: "heading", attrs: { level: 2 }, content: [t(text)] });
  const h3 = (text: string): JSONContent => ({ type: "heading", attrs: { level: 3 }, content: [t(text)] });

  it("reads the caret's section without blocks holding a password, at most 2000 characters", () => {
    let read: (() => string | null) | null = null;
    // As the page editor does: the last attached editor's reader counts, and only its own release withdraws it.
    const registerSection = vi.fn((reader: () => string | null) => {
      read = reader;
      return () => { if (read === reader) read = null; };
    });
    const { editor, controller } = withDouble([
      h2("Úvod"), p("Mimo sekci."),
      h2("Vzdálená správa"), p("Připojte se přes VPN."), p("Nouzový účet: heslo Srv#2026!rack"), h3("Konzole"), p("Pak otevřete konzoli."),
      h2("Další"), p("Jinde."),
    ], { registerSection });
    expect(read).toBeTypeOf("function");
    editor.commands.setTextSelection(blockAt(editor, "Připojte").pos + 3);
    expect(read!()).toBe("Vzdálená správa\n\nPřipojte se přes VPN.\n\nKonzole\n\nPak otevřete konzoli.");
    // A long section is cut around the caret.
    const long = withDouble([p("A".repeat(1500)), p(`Tady ${"B".repeat(1500)}`), p("C".repeat(1500))], { registerSection });
    long.editor.commands.setTextSelection(blockAt(long.editor, "Tady").pos + 2);
    const text = read!()!;
    expect(text.length).toBeLessThanOrEqual(2000);
    expect(text).toContain("Tady");
    // The first editor leaving keeps the second one's reader; the second leaving withdraws it.
    for (const done of [controller, long.controller]) controllers.splice(controllers.indexOf(done), 1);
    controller.destroy();
    expect(read).not.toBeNull();
    long.controller.destroy();
    expect(read).toBeNull();
  });
});

describe("link candidates", () => {
  it("skips pages already linked, the current page and disabled suggestion types", async () => {
    const linkCandidates = vi.fn(async () => [
      candidate("VPN", vpnPage, "Připojení k VPN"),
      candidate("Onboarding nováčků", currentPage, "Onboarding nováčků"),
      candidate("Zálohování serverů", backupPage, "Zálohování serverů"),
      candidate("Zálohování", onboardingPage, "Zálohování"),
    ]);
    const content = [
      p("Nastavte VPN, projděte Onboarding nováčků a Zálohování serverů."),
      p("Viz ", t("návod", [{ type: "link", attrs: { href: `/#/pages/${vpnPage}?heading=sit` } }]), "."),
    ];
    const { suggest } = withDouble(content, { linkCandidates });
    const offered = await suggest("Nastavte");
    // VPN is linked elsewhere in the page, the current page is never offered, and the longer phrase wins over the
    // word inside it („Zálohování serverů“ before „Zálohování“).
    expect(offered.map(item => [item.kind, item.title])).toEqual([
      ["link.mention", "Propojit „Zálohování serverů“ se stránkou „Zálohování serverů“"],
      ["link.mention", "Nahradit názvem stránky"],
    ]);
    expect(offered.every(item => item.icon === "link" || item.icon === "file-text")).toBe(true);
    expect(offered.some(item => item.ai)).toBe(false);

    // A page reference node counts as a link as well.
    const referenced = withDouble([p("Zálohování serverů běží v noci."), p("Viz ")],
      { linkCandidates: vi.fn(async () => [candidate("Zálohování serverů", backupPage, "Zálohování serverů")]) });
    expect(await referenced.suggest("běží")).toHaveLength(2);
    referenced.editor.commands.setTextSelection(referenced.editor.state.doc.content.size - 1);
    expect(referenced.controller.dispatch(insertPageReference({ id: backupPage, title: "Zálohování serverů", url: `#/pages/${backupPage}` }))).toBe(true);
    expect(await referenced.suggest("běží")).toEqual([]);

    // „Propojení zmínek“ switched off in Profil → AI: nothing is asked.
    const off = vi.fn(async () => [candidate("VPN", vpnPage, "Připojení k VPN")]);
    const disabled = withDouble([p("Nastavte VPN podle návodu.")], { linkCandidates: off, suggestionTypeEnabled: type => type !== "link-mention" });
    expect(await disabled.suggest("Nastavte")).toEqual([]);
    expect(off).not.toHaveBeenCalled();
    // Only a finished paragraph is analysed (not the idle pause while typing), and never one with a password.
    const idle = vi.fn(async () => []);
    const typing = withDouble([p("Nastavte VPN podle návodu."), p("Nouzový účet VPN: heslo Srv#2026!rack")], { linkCandidates: idle });
    expect(await typing.suggest("Nastavte", "idle")).toEqual([]);
    expect(await typing.suggest("Nouzový")).toEqual([]);
    expect(idle).not.toHaveBeenCalled();
  });

  it("stays lexical without the capability and silent without assist", async () => {
    const both = vi.fn(async () => [candidate("vzdálený přístup do sítě", vpnPage, "Připojení k VPN", "meaning"), candidate("SSO", onboardingPage, "Jednotné přihlášení", "alias")]);
    const lexical = withDouble([p("Pro vzdálený přístup do sítě použijte SSO.")], { linkCandidates: both, editorLinks: () => false });
    expect((await lexical.suggest("Pro vzdálený")).map(item => item.title)).toEqual(["Propojit „SSO“ se stránkou „Jednotné přihlášení“", "Nahradit názvem stránky"]);
    const meaning = withDouble([p("Pro vzdálený přístup do sítě použijte SSO.")], { linkCandidates: both, editorLinks: () => true });
    expect((await meaning.suggest("Pro vzdálený")).map(item => item.title)).toContain("Propojit „vzdálený přístup do sítě“ se stránkou „Připojení k VPN“");

    // The server answers 404 with the wiki's or the author's assistance off: no suggestion and no message.
    call.mockRejectedValue(new ApiError(404, "Nenalezeno."));
    const off = withDouble([p("Nastavte VPN podle návodu.")], { linkCandidates: undefined });
    expect(await off.suggest("Nastavte")).toEqual([]);
    expect(call).toHaveBeenCalledWith("/assist/link-candidates", expect.objectContaining({ method: "POST" }));
    expect(JSON.parse(String(call.mock.calls[0]![1]!.body))).toMatchObject({ pageId: currentPage, phrases: expect.arrayContaining(["VPN"]) });
    expect(off.context.warn).not.toHaveBeenCalled();
    expect(off.context.notify).not.toHaveBeenCalled();
  });

  it("links the words once: Propojit again is no change", async () => {
    // Editor 0.9.1 keeps a block's marker after a write that keeps the text; until the module retires applied
    // suggestions (editor feature/assist-w4), choosing „Propojit“ again must not write anything.
    const { editor, controller, suggest } = withDouble([p("Nastavte VPN podle návodu.")], { linkCandidates: async () => [candidate("VPN", vpnPage, "Připojení k VPN")] });
    const [link] = await suggest("Nastavte");
    const context = () => ({
      editor, controller, signal: new AbortController().signal, range: link!.range,
      dispatch: (command: never, options?: never) => controller.dispatch(command, options), propose: vi.fn(), effects: { conceal: vi.fn() },
    });
    expect(await link!.apply(context())).toBe(true);
    expect(linkHrefs(editor)).toEqual([["VPN", `#/pages/${vpnPage}`]]);
    expect(await link!.apply(context())).toBe(false);
    expect(linkHrefs(editor)).toEqual([["VPN", `#/pages/${vpnPage}`]]);
  });

  it("asks at most once in 3 s, aborts the previous request and never asks a known phrase again", async () => {
    vi.useFakeTimers();
    const signals: AbortSignal[] = [];
    const fetch = vi.fn((request: { phrases: readonly string[] }, signal: AbortSignal) => {
      signals.push(signal);
      return Promise.resolve(request.phrases.includes("VPN") ? [candidate("VPN", vpnPage, "Připojení k VPN")] : []);
    });
    const client = createLinkCandidateClient(fetch);
    const signal = new AbortController().signal;
    await expect(client.request("page", ["VPN", "Nastavte"], signal)).resolves.toEqual([candidate("VPN", vpnPage, "Připojení k VPN")]);
    // Known phrases come from memory: no request at all.
    await expect(client.request("page", ["VPN"], signal)).resolves.toHaveLength(1);
    expect(fetch).toHaveBeenCalledTimes(1);
    // A new paragraph within 3 s waits; a newer one replaces it before it is sent.
    const second = client.request("page", ["Zálohování"], signal);
    const third = client.request("page", ["Monitoring"], signal);
    await expect(second).rejects.toMatchObject({ name: "AbortError" });
    await vi.advanceTimersByTimeAsync(2_999);
    expect(fetch).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    await expect(third).resolves.toEqual([]);
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(fetch.mock.calls[1]![0]).toEqual({ pageId: "page", phrases: ["Monitoring"] });
  });
});
