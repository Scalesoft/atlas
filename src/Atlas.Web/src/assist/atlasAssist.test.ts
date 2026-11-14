import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import * as assistEntry from "@atlas/markdown/assist";
import { createMarkdownEditorController, loadMarkdownDialect, registerMarkdownBlockKeyboardHook, type MarkdownEditorController, type MarkdownPlugin } from "@atlas/markdown/core";
import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";
import { insertInlineSecretReference, insertSecretReference } from "@atlas/markdown/plugins/secrets";
import { createYouTrackPlugin, type YouTrackIssue } from "@atlas/markdown/plugins/youtrack";
import type { Editor, JSONContent } from "@tiptap/core";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { createDocumentWidgets } from "../documentWidgets";
import { createAtlasAssist } from "./atlasAssist";
import { createSecretExamples, fnv1a, sessionSecretExamples } from "./rules";
import type { AtlasAssistContext, PickedSecret, SecretPickRequest } from "./types";

// No phase 1 rule detects a formula (catalog `math.inline`, phase1: false); a later source does. The rules are wrapped
// so one test can hand the adapter such a hit and check its command.
const injected = vi.hoisted(() => ({ hits: [] as unknown[] }));
vi.mock("./rules", async original => {
  const actual = await original<typeof import("./rules")>();
  return { ...actual, detectIntents: (...args: Parameters<typeof actual.detectIntents>) => injected.hits.length ? injected.hits : actual.detectIntents(...args) };
});

// Plan §12 H.7. The mapping from rule intents to the editor's existing commands runs against a real editor in both
// modes: through a test double of the module's `createAssistPlugin`, and (pinned 0.9.0 or source mode)
// also end to end through the real `@atlas/markdown/assist` (marker, Alt+Enter, Enter, proposal panel, warning bar).

// Contacts (`a-` components) need the editor's on-demand dialect; these controllers have no editor host.
beforeAll(() => loadMarkdownDialect());
const password = "Srv#2026!rack";
const pageUuid = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
const jan: MarkdownContact = { name: "Jan Novák", emails: ["jan.novak@firma.example.test"], directoryId: "u1", kind: "internal", internalUser: { id: "u1", name: "Jan Novák" } };
const otherJan: MarkdownContact = { name: "Jan Novák", emails: ["jan.novak@supplier.example.test"], directoryId: "u2", kind: "internal", internalUser: { id: "u2", name: "Jan Novák" } };
const issue: YouTrackIssue = { id: "2-17", key: "ATL-17", title: "Čtečka karet hlásí chybu", url: "https://youtrack.example.com/issue/ATL-17", state: "In Progress" };

type Inline = JSONContent;
const t = (text: string): Inline => ({ type: "text", text });
const p = (...parts: (string | Inline)[]): JSONContent => {
  const content = parts.filter(part => part !== "").map(part => typeof part === "string" ? t(part) : part);
  return content.length ? { type: "paragraph", content } : { type: "paragraph" };
};
const lines = (...texts: string[]): JSONContent => ({ type: "paragraph", content: texts.flatMap((text, index) => [...index ? [{ type: "hardBreak" }] : [], t(text)]) });
const h2 = (text: string): JSONContent => ({ type: "heading", attrs: { level: 2 }, content: [t(text)] });

function fakeContext(overrides: Partial<AtlasAssistContext> = {}) {
  return {
    pageId: () => "page-1",
    contacts: { search: vi.fn(async () => [] as MarkdownContact[]), get: vi.fn(async () => null) },
    pages: { search: vi.fn(async () => []), resolve: vi.fn(async (id: string) => id === pageUuid ? { id, title: "Připojení k VPN", url: `#/pages/${id}` } : null) },
    resolveIssue: vi.fn(async (key: string) => key === "ATL-17" ? issue : null),
    hasSecrets: vi.fn(async () => true),
    pickSecret: vi.fn(async (_request: SecretPickRequest): Promise<PickedSecret | null> => ({ command: insertInlineSecretReference({ secretId: "0b6a0d9e-6e4e-4a8e-9a3f-1c2d3e4f5a61" }), inline: true, vaultName: "Provoz" })),
    notify: vi.fn(),
    warn: vi.fn(),
    examples: createSecretExamples(),
    ...overrides,
  } satisfies AtlasAssistContext;
}

/** The module's `createAssistPlugin` replaced by a recorder: the adapter's source and hooks without the module's UI. */
function moduleDouble() {
  const state: { options?: { sources: { suggest: (block: unknown, context: { signal: AbortSignal }) => Promise<Suggestion[]> }[]; onApplied?: (suggestion: Suggestion) => void } } = {};
  const module = {
    createAssistPlugin: (options: never) => { state.options = options; return { id: "assist", apiVersion: 1 as const, create: () => ({}) }; },
    openMarkdownProposalPanel: vi.fn(),
    defaultAssistStrings: {},
  };
  return { module, state };
}
interface Suggestion {
  id: string; kind: string; title: string; description?: string; icon: string; tone?: string; ai?: boolean; applyLabel?: string;
  range?: { from: number; to: number };
  apply(context: unknown): boolean | Promise<boolean>;
  secondary?: { id: string; label: string; run(context: unknown): void }[];
}

const controllers: MarkdownEditorController[] = [];
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
    plugins: [...createDocumentWidgets({ kind: "edit" }), createYouTrackPlugin({ client: { search: async () => [] } }), assistPlugin, capture],
    source: { kind: "local", value: { format: "atlas-rich", version: 1, document: { type: "doc", content } } },
  });
  controllers.push(controller);
  const host = document.createElement("div");
  document.body.append(host);
  controller.attachView(host, { ariaLabel: "Dokument" });
  return { editor, controller, host };
}

/** One character per document position, as the module hands it to sources. */
function blockText(node: ProseMirrorNode) {
  let text = "";
  node.forEach(child => { text += child.isText ? child.text! : child.type.name === "hardBreak" ? "\n" : "￼"; });
  return text;
}
function blockAt(editor: Editor, match: string, extra: { pasted?: string } = {}) {
  let pos = -1;
  let node: ProseMirrorNode | undefined;
  editor.state.doc.descendants((child, at) => {
    if (pos < 0 && child.isTextblock && child.textContent.includes(match)) { pos = at; node = child; }
    return pos < 0;
  });
  const text = blockText(node!);
  const rangeOf = (start: number, end: number) => ({ from: pos + 1 + start, to: pos + 1 + end });
  const pasted = extra.pasted ? rangeOf(text.indexOf(extra.pasted), text.indexOf(extra.pasted) + extra.pasted.length) : undefined;
  return { pos, node: node!, text, trigger: pasted ? "paste" as const : "leave" as const, ...(pasted ? { pasted } : {}), rangeOf };
}

/** The apply context the module builds, reduced to what Atlas uses: mapped range, dispatch on the span, proposal. */
const spanTexts = new WeakMap<Suggestion, string>();
const textAt = (editor: Editor, range: { from: number; to: number }) => editor.state.doc.textBetween(range.from, range.to, "\n", "￼");
function applyContext(editor: Editor, controller: MarkdownEditorController, suggestion: Suggestion) {
  // As the module does: the span's text is taken when the suggestion is made and re-checked on apply.
  const tracked = suggestion.range && { ...suggestion.range, text: spanTexts.get(suggestion) ?? textAt(editor, suggestion.range) };
  const abort = new AbortController();
  const proposals: { title: string; after: JSONContent; range?: { from: number; to: number } }[] = [];
  const context = {
    editor,
    controller,
    signal: abort.signal,
    get range() {
      if (!tracked || tracked.to > editor.state.doc.content.size) return null;
      return editor.state.doc.textBetween(tracked.from, tracked.to, "\n", "￼") === tracked.text ? { from: tracked.from, to: tracked.to } : null;
    },
    dispatch: vi.fn((command: never, options?: { replaceRange?: { from: number; to: number } }) => {
      if (options?.replaceRange) editor.commands.setTextSelection(options.replaceRange);
      return controller.dispatch(command, options);
    }),
    propose: vi.fn(async (proposal: { title: string; after: JSONContent; range?: { from: number; to: number } }) => {
      proposals.push(proposal);
      return editor.commands.insertContentAt(proposal.range!, proposal.after);
    }),
    effects: { conceal: vi.fn(async () => {}) },
  };
  return { context, proposals, abort };
}

function withDouble(content: JSONContent[], overrides: Partial<AtlasAssistContext> = {}) {
  const { module, state } = moduleDouble();
  const context = fakeContext(overrides);
  const mounted = mount(content, createAtlasAssist(module as never, context));
  const source = state.options!.sources[0]!;
  const suggest = async (match: string, extra: { pasted?: string } = {}, signal = new AbortController().signal) => {
    const found = await source.suggest(blockAt(mounted.editor, match, extra), { signal });
    for (const item of found) if (item.range) spanTexts.set(item, textAt(mounted.editor, item.range));
    return found;
  };
  const apply = async (suggestion: Suggestion) => {
    const prepared = applyContext(mounted.editor, mounted.controller, suggestion);
    const result = await suggestion.apply(prepared.context);
    if (result) state.options!.onApplied?.(suggestion);
    return { result, ...prepared };
  };
  return { ...mounted, context, state, suggest, apply };
}

const types = (editor: Editor) => {
  const found: string[] = [];
  editor.state.doc.descendants(node => { found.push(node.type.name); return true; });
  return found;
};

describe("intent to editor command (both editor modes)", () => {
  it("does not resolve or show disabled suggestion types while keeping secret warnings", async () => {
    const url = `https://wiki.example/#/pages/${pageUuid}`;
    const { suggest, context } = withDouble([
      p("Chyba je v ATL-17."), p("Kontaktujte Jana Nováka."), p(url),
      lines("Jméno;Role", "Jan;Správce", "Eva;Čtenář"), p(`heslo: ${password}`),
      ...Array.from({ length: 6 }, (_, i) => h2("Oddíl " + i)),
    ], { suggestionTypeEnabled: () => false });
    expect(await suggest("ATL-17")).toEqual([]);
    expect(await suggest("Kontaktujte")).toEqual([]);
    expect(await suggest(url, { pasted: url })).toEqual([]);
    expect(await suggest("Jméno")).toEqual([]);
    expect(await suggest("Oddíl 0")).toEqual([]);
    expect((await suggest("heslo")).map(item => item.kind)).toContain("secret.plaintext");
    expect(context.resolveIssue).not.toHaveBeenCalled();
    expect(context.contacts.search).not.toHaveBeenCalled();
    expect(context.pages.resolve).not.toHaveBeenCalled();
  });

  it("withdraws an existing suggestion when its type is disabled", async () => {
    let enabled = true;
    const { suggest } = withDouble([p("Chyba je v ATL-17.")], { suggestionTypeEnabled: () => enabled });
    const [suggestion] = await suggest("ATL-17");
    expect(suggestion?.kind).toBe("youtrack.issue");
    enabled = false;
    expect((suggestion as Suggestion & { available?: () => boolean }).available?.()).toBe(false);
  });

  it("replaces a resolved YouTrack key with the issue widget", async () => {
    const { editor, suggest, apply, context } = withDouble([p("Chyba čtečky je v ATL-17, řešíme ji.")]);
    const [suggestion, ...rest] = await suggest("ATL-17");
    expect(rest).toEqual([]);
    expect(suggestion).toMatchObject({ kind: "youtrack.issue", title: "Nahradit ATL-17 úkolem z YouTracku", description: "Čtečka karet hlásí chybu · In Progress", icon: "ticket" });
    const { result } = await apply(suggestion!);
    expect(result).toBe(true);
    expect(types(editor)).toContain("youtrackIssue");
    expect(editor.state.doc.textContent).not.toContain("ATL-17");
    expect(context.notify).toHaveBeenCalledWith("Úkol vložen", expect.any(Function));
  });

  it("explains an unverified typed YouTrack key without inventing an issue", async () => {
    const { editor, suggest } = withDouble([p("viz youtrack PX2-1234")]);
    const [notice, ...rest] = await suggest("PX2-1234");
    expect(rest).toEqual([]);
    expect(notice).toMatchObject({
      kind: "youtrack.unverified", title: "Úkol PX2-1234 se nepodařilo ověřit",
      range: { from: 14, to: 22 }, informational: true, icon: "ticket",
    });
    expect(notice!.description).toContain("Zkontrolujte klíč, nastavení integrace nebo oprávnění");
    expect(notice!.apply).toBeUndefined();
    expect(editor.state.doc.textContent).toBe("viz youtrack PX2-1234");
  });
  it("offers an email link for an address after a contact prompt when the directory has no match", async () => {
    const email = "Tedst@test.teswt";
    const { editor, suggest, apply } = withDouble([p("Prosím kontaktujte ", { type: "text", text: email, marks: [{ type: "bold" }] }, " kvůli návrhu.")]);
    const [suggestion, ...rest] = await suggest("kontaktujte");
    expect(rest).toEqual([]);
    expect(suggestion).toMatchObject({ kind: "contact.email-link", title: "Vložit e-mailový odkaz", range: { from: 20, to: 36 } });
    expect(suggestion!.description).toContain("není v adresáři");
    expect(await apply(suggestion!)).toMatchObject({ result: true });
    expect(editor.state.doc.textContent).toBe(`Prosím kontaktujte ${email} kvůli návrhu.`);
    const linked = editor.state.doc.child(0).child(1);
    expect(new Map(linked.marks.map(mark => [mark.type.name, mark.attrs.href]))).toEqual(new Map([["bold", undefined], ["link", `mailto:${email}`]]));
  });
  it("does not offer the email fallback when the address already links to mail", async () => {
    const email = "Tedst@test.teswt";
    const { suggest } = withDouble([p("Prosím kontaktujte ", { type: "text", text: email, marks: [{ type: "link", attrs: { href: `mailto:${email}` } }] }, " kvůli návrhu.")]);
    expect(await suggest("kontaktujte")).toEqual([]);
  });
  it("does not offer an email link when the address is inside a wider existing link", async () => {
    const email = "Tedst@test.teswt";
    const linkedText = `Prosím kontaktujte ${email} kvůli návrhu.`;
    const { editor, suggest } = withDouble([p({ type: "text", text: linkedText, marks: [{ type: "link", attrs: { href: "https://example.com/contact" } }] })]);
    expect(await suggest("kontaktujte")).toEqual([]);
    expect(editor.state.doc.child(0).child(0).marks.find(mark => mark.type.name === "link")?.attrs.href).toBe("https://example.com/contact");
  });
  it("offers an inline contact and a card for one person, written as an inflected name", async () => {
    const contacts = { search: vi.fn(async (query: string) => query === "novák" ? [jan] : []), get: vi.fn(async () => null) };
    const { editor, suggest, apply } = withDouble([p("Pro přístup kontaktujte Jana Nováka z provozu.")], { contacts });
    const suggestions = await suggest("kontaktujte");
    expect(suggestions.map(item => [item.kind, item.title, item.icon])).toEqual([
      ["contact.inline", "Nahradit „Jana Nováka“ kontaktem", "user"],
      ["contact.card", "Vložit kartu kontaktu pod odstavec", "users"],
    ]);
    expect(suggestions[0]!.description).toBe("Jan Novák · jan.novak@firma.example.test");
    expect(contacts.search.mock.calls.map(([query]) => query)).toEqual(["Jana Nováka", "novák"]);
    await apply(suggestions[0]!);
    expect(types(editor)).toContain("contactInline");
    expect(editor.state.doc.textContent).toContain("Pro přístup kontaktujte");
    expect(editor.state.doc.textContent).not.toContain("Jana Nováka");
  });

  it("puts the contact card under the paragraph", async () => {
    const contacts = { search: vi.fn(async () => [jan]), get: vi.fn(async () => null) };
    const { editor, suggest, apply } = withDouble([p("Obraťte se na Jan Novák kvůli přístupu."), p("Další odstavec")], { contacts });
    const card = (await suggest("Obraťte")).find(item => item.kind === "contact.card")!;
    await apply(card);
    expect(editor.state.doc.child(0).textContent).toBe("Obraťte se na Jan Novák kvůli přístupu.");
    expect(editor.state.doc.child(1).type.name).toBe("contactCard");
    expect(editor.state.doc.child(2).textContent).toBe("Další odstavec");
  });

  it("lets the author pick from the directory when several people match", async () => {
    const contacts = { search: vi.fn(async () => [jan, otherJan]), get: vi.fn(async () => null) };
    const { editor, suggest, apply, context } = withDouble([p("Kontaktujte Jan Novák.")], { contacts });
    const [pick, ...rest] = await suggest("Kontaktujte");
    expect(rest).toEqual([]);
    expect(pick).toMatchObject({ kind: "contact.inline", title: "Vložit kontakt — vybrat z adresáře…", icon: "user" });
    await apply(pick!);
    // The existing `@` list opens with the name typed; nothing is inserted on the author's behalf.
    expect(editor.state.doc.textContent).toBe("Kontaktujte @Jan Novák.");
    expect(context.notify).not.toHaveBeenCalled();
  });

  it("turns a pasted page address into a page reference", async () => {
    const url = `https://wiki.firma.example.test/#/pages/${pageUuid}`;
    const { editor, suggest, apply, context } = withDouble([p(`Postup je tady: ${url}`)]);
    expect(await suggest("Postup")).toEqual([]);
    const [link] = await suggest("Postup", { pasted: url });
    expect(link).toMatchObject({ kind: "page.link", title: "Nahradit adresu odkazem na stránku „Připojení k VPN“", icon: "link" });
    expect(context.pages.resolve).toHaveBeenCalledWith(pageUuid, expect.anything());
    await apply(link!);
    expect(types(editor)).toContain("pageReference");
    expect(editor.state.doc.textContent).not.toContain("https://");
  });

  it("wraps a warning paragraph into an information box of its variant", async () => {
    const { editor, suggest, apply } = withDouble([p("Upozornění: server se v noci restartuje.")]);
    const [callout] = await suggest("Upozornění");
    expect(callout).toMatchObject({ kind: "callout.wrap", description: "Upozornění", icon: "callout" });
    await apply(callout!);
    const box = editor.state.doc.child(0);
    expect(box.type.name).toBe("callout");
    expect(box.attrs.variant).toBe("warning");
    expect(box.textContent).toBe("Upozornění: server se v noci restartuje.");
  });

  it("turns a separated paragraph into a table through the proposal panel", async () => {
    const { editor, suggest, apply } = withDouble([lines("Server;IP;Patro", "srv-01;10.0.0.11;2", "srv-02;10.0.0.12;3")]);
    const [table] = await suggest("Server");
    expect(table).toMatchObject({ kind: "table.fromText", title: "Převést na tabulku", icon: "table" });
    const { proposals } = await apply(table!);
    expect(proposals).toHaveLength(1);
    expect(proposals[0]!.after.type).toBe("table");
    expect(editor.state.doc.child(0).type.name).toBe("table");
    expect(editor.state.doc.child(0).childCount).toBe(3);
    expect(editor.state.doc.child(0).child(1).textContent).toBe("srv-0110.0.0.112");
  });

  it("turns task lines into a checklist and keeps the item text", async () => {
    const { editor, suggest, apply } = withDouble([lines("[ ] Zálohovat databázi", "TODO: restartovat server")]);
    const [tasks] = await suggest("Zálohovat");
    expect(tasks).toMatchObject({ kind: "tasklist.fromText", icon: "list-checks" });
    await apply(tasks!);
    const list = editor.state.doc.child(0);
    expect(list.type.name).toBe("taskList");
    expect([...Array(list.childCount).keys()].map(index => list.child(index).textContent)).toEqual(["Zálohovat databázi", "restartovat server"]);
  });

  it("turns code lines into a code block with its language", async () => {
    const { editor, suggest, apply } = withDouble([lines("const host = 'srv-01';", "if (host) {", "  console.log(host);", "}")]);
    const [code] = await suggest("const host");
    expect(code).toMatchObject({ kind: "code.fromText", description: "Jazyk javascript", icon: "square-code" });
    await apply(code!);
    expect(editor.state.doc.child(0).type.name).toBe("codeBlock");
    expect(editor.state.doc.child(0).attrs.language).toBe("javascript");
    expect(editor.state.doc.child(0).textContent).toBe("const host = 'srv-01';\nif (host) {\n  console.log(host);\n}");
  });

  it("previews a diagram from steps joined by arrows", async () => {
    const { editor, suggest, apply } = withDouble([p("Žádost -> Schválení vedoucím -> Přístup")]);
    const [diagram] = await suggest("Žádost");
    expect(diagram).toMatchObject({ kind: "diagram.fromText", title: "Vytvořit diagram", icon: "workflow" });
    const { proposals } = await apply(diagram!);
    expect(proposals[0]!.after.type).toBe("diagram");
    const node = editor.state.doc.child(0);
    expect(node.type.name).toBe("diagram");
    expect(node.attrs.data.source).toBe('flowchart LR\n  s1["Žádost"]\n  s2["Schválení vedoucím"]\n  s3["Přístup"]\n  s1 --> s2\n  s2 --> s3');
  });

  it("inserts a table of contents at the start of a document with six H2 headings", async () => {
    const headings = ["Přístup", "Síť", "Zálohy", "Monitoring", "Kontakty", "Havárie"].map(h2);
    const { editor, suggest, apply } = withDouble([p("Úvod"), ...headings]);
    const [toc] = await suggest("Havárie");
    expect(toc).toMatchObject({ kind: "toc.insert", title: "Vložit obsah dokumentu na začátek", icon: "outline" });
    await apply(toc!);
    expect(editor.state.doc.child(0).type.name).toBe("tableOfContents");
    expect(await suggest("Havárie")).toEqual([]);
  });

  it("offers a vault reference only when the reader may discover one and inserts it after the words", async () => {
    const pickSecret = vi.fn(async (_request: SecretPickRequest): Promise<PickedSecret | null> => ({ command: insertInlineSecretReference({ secretId: "5c1f6b7a-2d3e-4f50-8a9b-0c1d2e3f4a5b" }), inline: true, vaultName: "Síť" }));
    const hasSecrets = vi.fn(async (query: string) => query.includes("Lhota"));
    const { editor, suggest, apply } = withDouble([p("Heslo k VPN Lhota je uložené v KeePassu.")], { pickSecret, hasSecrets });
    const [reference] = await suggest("Heslo");
    expect(reference).toMatchObject({ kind: "secret.reference", title: "Vložit odkaz na heslo z trezoru…", icon: "key-round" });
    expect(hasSecrets).toHaveBeenCalledWith("VPN Lhota", expect.any(AbortSignal));
    await apply(reference!);
    expect(pickSecret).toHaveBeenCalledWith(expect.objectContaining({ mode: "pick", initialQuery: "VPN Lhota" }));
    expect(types(editor)).toContain("secretInlineReference");
    expect(editor.state.doc.textContent.startsWith("Heslo ")).toBe(true);
    hasSecrets.mockResolvedValue(false);
    expect(await suggest("Heslo")).toEqual([]);
  });

  it("inserts a picked secret card under the block", async () => {
    const pickSecret = vi.fn(async (): Promise<PickedSecret | null> => ({ command: insertSecretReference({ secretId: "5c1f6b7a-2d3e-4f50-8a9b-0c1d2e3f4a5b" }), inline: false }));
    const { editor, suggest, apply } = withDouble([p("Heslo k VPN Lhota je uložené v KeePassu."), p("Konec")], { pickSecret });
    const [reference] = await suggest("Heslo");
    await apply(reference!);
    expect(editor.state.doc.child(0).textContent).toBe("Heslo k VPN Lhota je uložené v KeePassu.");
    expect(editor.state.doc.child(1).type.name).toBe("secretReference");
    expect(editor.state.doc.child(2).textContent).toBe("Konec");
  });

  it("replaces a formula span with inline math", async () => {
    const { editor, suggest, apply } = withDouble([p("Energie je E=mc^2 podle vzorce.")]);
    const start = "Energie je ".length;
    injected.hits = [{ intent: "math.inline", span: { start, end: start + "E=mc^2".length }, query: "E=mc^2" }];
    try {
      const [math] = await suggest("Energie");
      expect(math).toMatchObject({ kind: "math.inline", title: "Nahradit „E=mc^2“ vzorcem", icon: "sigma" });
      await apply(math!);
    } finally { injected.hits = []; }
    expect(types(editor)).toContain("mathInline");
    expect(editor.state.doc.textContent).toBe("Energie je  podle vzorce.");
  });

  it("never shows the AI glyph or the AI tag on a rule suggestion", async () => {
    const contacts = { search: vi.fn(async () => [jan]), get: vi.fn(async () => null) };
    const { suggest } = withDouble([p(`Kontaktujte Jan Novák kvůli ATL-17, heslo: ${password}`)], { contacts });
    const all = await suggest("Kontaktujte");
    expect(all.map(item => item.kind)).toEqual(["secret.plaintext", "youtrack.issue", "contact.inline", "contact.card"]);
    for (const item of all) {
      expect(item.icon).not.toBe("sparkles");
      expect(item.ai).toBeUndefined();
    }
  });
});

describe("YouTrack candidates (decision O4: resolve with a negative cache)", () => {
  it("shows an informational result for a missing issue and stays silent on a failed request", async () => {
    const resolveIssue = vi.fn(async (key: string) => {
      if (key === "OPS-5") throw new Error("YouTrack je nedostupný.");
      return null;
    });
    const { suggest } = withDouble([p("Viz UTF-8, ABC-9 a OPS-5.")], { resolveIssue });
    const [missing, ...rest] = await suggest("Viz");
    expect(rest).toEqual([]);
    expect(missing).toMatchObject({ kind: "youtrack.unverified", informational: true, title: "Úkol ABC-9 se nepodařilo ověřit" });
    expect(missing?.apply).toBeUndefined();
    expect(resolveIssue.mock.calls.map(([key]) => key)).toEqual(["ABC-9", "OPS-5"]);
    // ABC-9 is known missing for the session; OPS-5 failed and is asked again.
    expect((await suggest("Viz")).map(item => item.kind)).toEqual(["youtrack.unverified"]);
    expect(resolveIssue.mock.calls.map(([key]) => key)).toEqual(["ABC-9", "OPS-5", "OPS-5"]);
  });

  it("asks YouTrack at most once at a time", async () => {
    let running = 0;
    let most = 0;
    const resolveIssue = vi.fn(async (key: string) => {
      most = Math.max(most, ++running);
      await new Promise(resolve => setTimeout(resolve, 5));
      running--;
      return { ...issue, id: key, key };
    });
    const { suggest } = withDouble([p("Souvisí ATL-1, ATL-2 a OPS-3.")], { resolveIssue });
    expect((await suggest("Souvisí")).map(item => item.kind)).toEqual(["youtrack.issue", "youtrack.issue", "youtrack.issue"]);
    expect(most).toBe(1);
  });
});

describe("a plaintext password (E7, replace mode)", () => {
  it("warns persistently and moves the password to an inline secret reference", async () => {
    const { editor, suggest, apply, context } = withDouble([p(`Nouzový účet serverovny: admin, heslo ${password}`)]);
    const [warning, ...rest] = await suggest("Nouzový");
    expect(rest).toEqual([]);
    expect(warning).toMatchObject({
      kind: "secret.plaintext", title: "Možné heslo v textu", tone: "warning", icon: "triangle-alert",
      applyLabel: "Přesunout do trezoru", description: "Detekce běží jen ve vašem prohlížeči, modelu se nic neposílá.",
    });
    expect(warning!.secondary!.map(action => action.label)).toEqual(["Je to jen příklad"]);
    expect(warning!.id).toBe(`secret.plaintext:${fnv1a(password)}`);
    const { result, context: applied } = await apply(warning!);
    expect(result).toBe(true);
    expect(context.pickSecret).toHaveBeenCalledWith(expect.objectContaining({ mode: "replace", initialQuery: "Nouzový účet serverovny admin" }));
    expect(applied.effects.conceal).toHaveBeenCalledTimes(1);
    expect(types(editor)).toContain("secretInlineReference");
    expect(editor.state.doc.textContent).toBe("Nouzový účet serverovny: admin, heslo ");
    expect(context.notify).toHaveBeenCalledWith("Heslo je v trezoru Provoz. V textu zůstal jen odkaz.", expect.any(Function));
  });

  it("never puts the value into a request, a log or storage", async () => {
    const fetch = vi.spyOn(globalThis, "fetch");
    const logs = (["log", "info", "warn", "error", "debug"] as const).map(level => vi.spyOn(console, level));
    const storage = vi.spyOn(Storage.prototype, "setItem");
    const contacts = { search: vi.fn(async () => [jan]), get: vi.fn(async () => null) };
    const { suggest, apply, context } = withDouble([p(`Kontaktujte Jan Novák, heslo: ${password} a token sk-live0123456789abcdefXYZ`)], { contacts });
    const suggestions = await suggest("Kontaktujte");
    for (const suggestion of suggestions.filter(item => item.kind === "secret.plaintext")) await apply(suggestion);
    const calls = [
      ...Object.values(context).filter(value => typeof value === "function" && "mock" in value).flatMap(spy => (spy as ReturnType<typeof vi.fn>).mock.calls),
      ...contacts.search.mock.calls, ...fetch.mock.calls, ...logs.flatMap(spy => spy.mock.calls), ...storage.mock.calls,
    ];
    const seen = JSON.stringify(calls, (_key, value) => value instanceof AbortSignal ? "signal" : value);
    expect(seen).not.toContain(password);
    expect(seen).not.toContain("sk-live0123456789abcdefXYZ");
    expect(fetch).not.toHaveBeenCalled();
  });

  it("does not look up the vault from a block that holds a password", async () => {
    const { suggest, context } = withDouble([p(`Heslo k VPN je uložené v KeePassu, dočasně heslo: ${password}`)]);
    expect((await suggest("Heslo")).map(item => item.kind)).toEqual(["secret.plaintext"]);
    expect(context.hasSecrets).not.toHaveBeenCalled();
  });

  it("„Je to jen příklad“ remembers a hash in the session memory and stops warning on this page", async () => {
    const { editor, controller, suggest } = withDouble([p(`Například heslo: ${password}`)], { examples: undefined, pageId: () => "page-example" });
    const [warning] = await suggest("Například");
    const example = warning!.secondary!.find(action => action.label === "Je to jen příklad")!;
    example.run(applyContext(editor, controller, warning!).context);
    expect(sessionSecretExamples.isExample("page-example", password)).toBe(true);
    expect(sessionSecretExamples.isExample("another-page", password)).toBe(false);
    expect(await suggest("Například")).toEqual([]);
  });

  it("drops the move when the span changed while the picker was open", async () => {
    let answer!: (picked: PickedSecret | null) => void;
    const pickSecret = vi.fn(() => new Promise<PickedSecret | null>(resolve => { answer = resolve; }));
    const { editor, suggest, apply, context } = withDouble([p(`Účet admin, heslo ${password}`)], { pickSecret });
    const [warning] = await suggest("Účet");
    const pending = apply(warning!);
    await vi.waitFor(() => expect(pickSecret).toHaveBeenCalled());
    const at = editor.state.doc.textContent.indexOf(password) + 1;
    editor.commands.insertContentAt(at + 3, "X");
    answer({ command: insertInlineSecretReference({ secretId: "0b6a0d9e-6e4e-4a8e-9a3f-1c2d3e4f5a61" }), inline: true });
    const { result } = await pending;
    expect(result).toBe(false);
    expect(context.warn).toHaveBeenCalledWith("Návrh už neodpovídá textu.");
    expect(types(editor)).not.toContain("secretInlineReference");
  });
});

describe("stale suggestions", () => {
  it("drops a suggestion whose span changed before apply", async () => {
    const { editor, suggest, apply } = withDouble([p("Chyba čtečky je v ATL-17, řešíme ji.")]);
    const [suggestion] = await suggest("ATL-17");
    const at = editor.state.doc.textContent.indexOf("ATL-17") + 1;
    editor.commands.insertContentAt(at + 4, "9");
    const { result, context } = await apply(suggestion!);
    expect(result).toBe(false);
    expect(context.dispatch).not.toHaveBeenCalled();
    expect(types(editor)).not.toContain("youtrackIssue");
  });
});


describe("with the editor's assist module", () => {
  beforeEach(() => { vi.useFakeTimers(); });
  const settle = () => vi.advanceTimersByTimeAsync(0);
  const key = (editor: Editor, name: string, init: KeyboardEventInit = {}) =>
    editor.view.dom.dispatchEvent(new KeyboardEvent("keydown", { key: name, bubbles: true, cancelable: true, ...init }));
  const markers = () => [...document.querySelectorAll<HTMLButtonElement>(".md-assist-marker:not([data-leaving])")];
  const listbox = () => document.querySelector<HTMLElement>(".md-assist-menu [role=listbox]");
  const intentions = () => document.querySelector<HTMLElement>("[data-testid=intentions]");
  const caretInto = (editor: Editor, match: string) => editor.commands.setTextSelection(blockAt(editor, match).pos + 2);
  function live(content: JSONContent[], overrides: Partial<AtlasAssistContext> = {}) {
    const context = fakeContext(overrides);
    return { ...mount(content, createAtlasAssist(assistEntry as never, context)), context };
  }

  it("replaces a resolved YouTrack key with an issue in one undo step", async () => {
    const { editor, controller, context } = live([p("")]);
    editor.view.focus();
    editor.commands.insertContent("Chyba čtečky je v ATL-17.");
    await vi.advanceTimersByTimeAsync(1500);
    await settle();
    expect(markers()).toHaveLength(1);
    expect(markers()[0]!.getAttribute("aria-label")).toBe("Návrh: Nahradit ATL-17 úkolem z YouTracku");
    key(editor, "Enter", { altKey: true });
    await settle();
    // Alt+Enter is the editor's „Co s tím jde dělat“, with the block's suggestions first.
    expect(intentions()!.textContent).toContain("Čtečka karet hlásí chybu · In Progress");
    key(editor, "Enter");
    await settle();
    expect(types(editor)).toContain("youtrackIssue");
    expect(context.notify).toHaveBeenCalledWith("Úkol vložen", expect.any(Function));
    controller.dispatch({ type: "undo" });
    expect(editor.getText()).toBe("Chyba čtečky je v ATL-17.");
    expect(types(editor)).not.toContain("youtrackIssue");
  });

  it("shows an unverified typed YouTrack key as a marker and explanation without writing an issue", async () => {
    const { editor, controller } = live([p("")]);
    editor.view.focus();
    editor.commands.insertContent("viz youtrack PX2-1234");
    await vi.advanceTimersByTimeAsync(1500);
    await settle();
    expect(markers()).toHaveLength(1);
    expect(markers()[0]!.getAttribute("aria-label")).toContain("nepodařilo ověřit");
    // An explanation writes nothing, so it is the marker's list, not a row of „Co s tím jde dělat“.
    markers()[0]!.dispatchEvent(new PointerEvent("pointerdown"));
    markers()[0]!.click();
    await settle();
    expect(listbox()!.textContent).toContain("Zkontrolujte klíč, nastavení integrace nebo oprávnění.");
    const before = controller.getValue();
    key(editor, "Enter");
    await settle();
    expect(controller.getValue()).toEqual(before);
    expect(types(editor)).not.toContain("youtrackIssue");
  });
  it("turns a tab separated paragraph into a table through the proposal panel", async () => {
    const { editor, controller } = live([lines("Server\tIP\tPatro", "srv-01\t10.0.0.11\t2", "srv-02\t10.0.0.12\t3"), p("Konec")]);
    editor.view.focus();
    caretInto(editor, "Server");
    key(editor, "Enter", { altKey: true });
    await settle();
    expect(intentions()!.textContent).toContain("Převést na tabulku");
    key(editor, "Enter");
    await settle();
    const panel = document.querySelector<HTMLElement>(".md-assist-proposal")!;
    expect(panel.textContent).toContain("Převést na tabulku");
    expect(editor.state.doc.child(0).type.name).toBe("paragraph");
    [...panel.querySelectorAll("button")].find(button => button.textContent === "Použít")!.click();
    await vi.advanceTimersByTimeAsync(1000);
    expect(editor.state.doc.child(0).type.name).toBe("table");
    controller.dispatch({ type: "undo" });
    expect(editor.state.doc.child(0).type.name).toBe("paragraph");
    expect(editor.state.doc.child(0).textContent).toBe("Server\tIP\tPatrosrv-01\t10.0.0.11\t2srv-02\t10.0.0.12\t3");
  });

  it("moves a plaintext password to an inline secret reference in replace mode", async () => {
    const { editor, host, controller, context } = live([p("Nouzový účet: admin"), p("Konec")]);
    editor.view.focus();
    caretInto(editor, "Nouzový");
    editor.commands.setTextSelection(blockAt(editor, "Nouzový").pos + 1 + "Nouzový účet: admin".length);
    editor.commands.insertContent(`, heslo ${password}`);
    caretInto(editor, "Konec");
    await settle();
    expect(markers()).toHaveLength(1);
    expect(markers()[0]!.dataset.tone).toBe("warning");
    const bar = host.querySelector<HTMLElement>(".md-assist-warning")!;
    expect(bar.getAttribute("role")).toBe("status");
    expect(bar.textContent).toContain("Možné heslo v textu");
    expect(host.querySelector(".md-assist-tone-warning")?.textContent).toBe(password);
    [...bar.querySelectorAll("button")].find(button => button.textContent?.includes("Přesunout do trezoru"))!.click();
    await settle();
    expect(context.pickSecret).toHaveBeenCalledWith(expect.objectContaining({ mode: "replace", initialQuery: "Nouzový účet admin" }));
    await vi.advanceTimersByTimeAsync(1000);
    expect(types(editor)).toContain("secretInlineReference");
    expect(editor.state.doc.textContent).not.toContain(password);
    expect(context.notify).toHaveBeenCalledWith("Heslo je v trezoru Provoz. V textu zůstal jen odkaz.", expect.any(Function));
    expect(JSON.stringify(controller.getValue())).not.toContain(password);
    // „Zpět“ on the toast (P6) reverts exactly the move, as Ctrl+Z would.
    (vi.mocked(context.notify).mock.calls.at(-1)![1] as () => void)();
    expect(types(editor)).not.toContain("secretInlineReference");
    expect(editor.state.doc.textContent).toContain(password);
  });

  it("keeps the author's caret in the next paragraph when a finished one is changed", async () => {
    const { editor, controller } = live([p("")]);
    editor.view.focus();
    editor.commands.insertContent("Chyba čtečky je v ATL-17.");
    await vi.advanceTimersByTimeAsync(1500);
    editor.commands.splitBlock();
    await settle();
    expect(markers()).toHaveLength(1);
    key(editor, "Enter", { altKey: true });
    await settle();
    key(editor, "Enter");
    await settle();
    expect(types(editor)).toContain("youtrackIssue");
    editor.commands.insertContent("Další věta.");
    expect(editor.state.doc.child(0).textContent).not.toContain("Další věta.");
    expect(editor.state.doc.lastChild!.textContent).toBe("Další věta.");
    controller.dispatch({ type: "undo" });
    expect(editor.state.doc.lastChild!.textContent).toBe("");
  });

  it("puts the block's markers on the Alt+F10 path of the block grip", async () => {
    const { editor } = live([p("Chyba čtečky je v ATL-17."), p("Konec")]);
    editor.view.focus();
    caretInto(editor, "Chyba");
    key(editor, "Enter", { altKey: true });
    await settle();
    key(editor, "Escape");
    await settle();
    const { markersOf } = await import("./atlasAssist");
    expect(markersOf(editor, 0)).toEqual(markers());
    expect(() => registerMarkdownBlockKeyboardHook(editor, { id: "atlas-assist-markers", controls: () => [] })).toThrow(/Duplicate/);
  });
});
