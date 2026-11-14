import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import type { Editor } from "@tiptap/core";
import type { MarkdownEditorController, MarkdownPlugin, MarkdownRichValue } from "@atlas/markdown/core";
import { MarkdownEditor } from "@atlas/markdown/vue";
import { atlasContentPlugins } from "../documentWidgets";
import { createHeadingFoldPlugin } from "../editor/headingFoldPlugin";
import { useModules } from "../composables/useModules";
import type { ProjectorHost } from "./requirementsPlugin";
import type { CatalogDefinition } from "./projectorApi";
import { sectionTarget } from "./requirementHeading";

const PAGE = "0b5f3c1e-0000-4000-8000-0000000000a1";
type Json ={ type: string; attrs?: Record<string, unknown>; content?: Json[]; text?: string; marks?: { type: string }[] };
const p = (text = ""): Json => ({ type: "paragraph", ...(text ? { content: [{ type: "text", text }] } : {}) });
/** A heading of the page: plain, or (with `id`) a requirement heading. */
const hd = (level: number, text: string, id?: string, attrs: Record<string, unknown> = {}): Json =>
  ({ type: "heading", attrs: { level, ...(id ? { headingId: id, state: "planned", direction: "internal", system: "", from: "", ...attrs } : {}) }, content: [{ type: "text", text }] });
const page = (...content: Json[]): MarkdownRichValue =>
  ({ format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 1 }, { id: "atlas-requirements", version: 4 }, { id: "pages", version: 1 }, { id: "contacts", version: 2 }], document: { type: "doc", content } } as MarkdownRichValue);

const toaster = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), dismissAll: vi.fn() }));
vi.mock("../composables/useToast", () => ({ useToast: () => toaster }));
const lexicon = vi.hoisted(() => (spaceId?: string) => spaceId === "space-1" ? { version: "1", truncated: false, people: [], pages: [{ id: "servers-page", title: "Servery" }], youTrackProjects: [] } : undefined);
vi.mock("../linking/lexicon", async importOriginal => ({ ...(await importOriginal<object>()), lexiconOf: lexicon, lexiconReady: async (spaceId?: string) => lexicon(spaceId) }));

const api = {
  catalog: vi.fn(async (_spaceId: string, _signal?: AbortSignal) => ({ definitions: [] as CatalogDefinition[], warnings: [] as string[] })),
  // The blocks of the keys asked for, from the catalog the last list came from (a test may answer the list without them).
  definitions: vi.fn(async (_spaceId: string, keys: readonly string[]) => {
    const { definitions } = await (api.catalog.mock.results.at(-1)?.value as ReturnType<typeof api.catalog>);
    return { definitions: definitions.filter(definition => keys.includes(definition.key)), warnings: [] as string[] };
  }),
  setCatalogPage: vi.fn(async (pageId: string, isCatalog: boolean) => ({ pageId, isCatalog })),
  createDeployment: vi.fn(async () => ({ id: "new-page", title: "Kostelec" })),
};
const catalogFlag = ref<boolean>();
const host: ProjectorHost = { spaceId: () => "space-1", pageId: () => PAGE, parentId: () => "parent-1", pageTitle: () => "KBN — Production", api, open: vi.fn(),
  unsaved: () => false, isCatalog: () => catalogFlag.value, setCatalog: vi.fn((isCatalog: boolean) => { catalogFlag.value = isCatalog; }) };

let wrapper: VueWrapper | undefined;
let editor!: Editor;
let controller!: MarkdownEditorController<"rich">;
const probe: MarkdownPlugin = { id: "requirements-view-probe", apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor; } }) };

async function open(value: MarkdownRichValue) {
  const model = ref(value);
  const plugins = [...atlasContentPlugins({ kind: "edit", projector: host, canWrite: () => true }), createHeadingFoldPlugin(() => PAGE), probe];
  const Host = defineComponent({ setup: () => () => h("div", {}, [h(MarkdownEditor, {
    profile: "rich", modelValue: model.value, "onUpdate:modelValue": ((next: MarkdownRichValue) => { model.value = next; }) as never,
    plugins, ariaLabel: "Stránka", readonly: false,
    onCreated: ((created: MarkdownEditorController<"rich">) => { controller = created; }) as never,
  })]) });
  wrapper = mount(Host, { attachTo: document.body });
  await vi.waitFor(() => expect(editor && !editor.isDestroyed && document.querySelector(".ProseMirror")).toBeTruthy());
  return wrapper;
}

/** The page's requirement headings in order: id, title, level, state and the ids their dependency line links to. */
const headings = () => {
  const found: { id: string; title: string; level: number; state: string; deps: string[]; depth: number }[] = [];
  const { doc } = editor.state;
  doc.forEach((node, _pos, index) => {
    if (node.type.name !== "heading" || node.attrs.state == null) return;
    const next = index + 1 < doc.childCount ? doc.child(index + 1) : null, deps: string[] = [];
    if (next?.type.name === "paragraph" && next.attrs.dependencies === true) next.descendants(child => { if (child.type.name === "pageReference") deps.push(sectionTarget(child.attrs.url)!.headingId); });
    found.push({ id: node.attrs.headingId, title: node.textContent, level: node.attrs.level, state: node.attrs.state, deps, depth: 0 });
  });
  return found;
};
const titles = () => headings().map(entry => entry.title);
const popover = () => document.querySelector<HTMLElement>(".md-context-popover")!;
const slash = (id: string) => controller.actions.slash.find(action => action.id === id);
/** „Moduly produktu“ as the page menu has it: enabled whatever the slash menu's rank. */
const catalogCommand = () => controller.actions.toolbar.find(action => action.id === "requirements.catalog")!.command;

beforeAll(async () => {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ modules: [{ id: "projector", version: 1 }, { id: "boards", version: 1 }] }), { headers: { "Content-Type": "application/json" } })));
  await useModules().start();
});
afterAll(() => { useModules().stop(); vi.unstubAllGlobals(); });
beforeEach(() => { vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(new Date("2026-10-27T10:00:00Z")); });
afterEach(() => { vi.restoreAllMocks(); catalogFlag.value = undefined; wrapper?.unmount(); wrapper = undefined; editor = undefined as never; document.body.replaceChildren(); vi.useRealTimers(); vi.clearAllMocks(); });

describe("inserting requirements", () => {
  it("are offered only with the projector module", async () => {
    await open(page(p()));
    expect(slash("requirements.insert")?.label).toBe("Požadavek");
    expect(slash("requirements.catalog")?.label).toBe("Moduly produktu");
    // (the summary slash and the board exclusions are not about the catalog: not ported)
  });

  it("offer the catalog once, first in the slash menu, in a space that has one, known before the first slash; none in a space without one (U4-13)", async () => {
    const { rankSuggestions } = await import("@atlas/markdown/core");
    const offered = (typed: string) => rankSuggestions(typed, controller.actions.slash.filter(action => controller.query(action.command).enabled),
      action => ({ title: action.label, keywords: action.keywords })).map(action => action.label);
    const focus = async () => { editor.view.dom.tabIndex = 0; editor.view.dom.focus(); await flushPromises(); };
    await open(page(p()));
    expect(api.catalog).not.toHaveBeenCalled();
    await focus();
    expect(api.catalog).toHaveBeenCalledTimes(1);
    for (const typed of ["Požadavek", "pozadavek"]) {
      expect(offered(typed)[0]).toBe("Požadavek");
      expect(offered(typed)).not.toContain("Moduly produktu");
    }
    api.catalog.mockResolvedValue({ definitions: [{ key: "vpn", title: "VPN", titleMarkdown: "VPN", direction: "inbound", requires: [], pageId: "c", bundle: "" }], warnings: [] });
    wrapper!.unmount();
    await open(page(p()));
    await focus();
    for (const typed of ["Požadavek", "pozadavek"]) expect(offered(typed).slice(0, 2)).toEqual(["Požadavek", "Moduly produktu"]);
    for (const typed of ["modul", "katalog", "produkt"]) expect(offered(typed)[0]).toBe("Moduly produktu");
    expect(controller.actions.slash.filter(action => action.label === "Moduly produktu")).toHaveLength(1);
    expect(controller.actions.toolbar.filter(action => action.label === "Moduly produktu").map(action => controller.query(action.command).enabled)).toEqual([true]);
  });

  it("insert a requirement heading (neřešeno) with the caret in it", async () => {
    // A new requirement is a sibling of the requirement above it.
    await open(page(hd(2, "SMS brána", "sms-brana"), p()));
    editor.commands.setTextSelection(editor.state.doc.content.size - 1);
    controller.dispatch(slash("requirements.insert")!.command);
    await flushPromises();
    expect(editor.state.selection.$from.parent.type.name).toBe("heading");
    expect(editor.state.selection.$from.parent.attrs).toMatchObject({ state: "planned", level: 2 });
  });

  it("from the catalog, ranked, with what the selection adds and why shown live, inserted on Enter as headings with the catalog's ids", async () => {
    const definition = (key: string, title: string, requires: string[] = []) => ({ key, title, titleMarkdown: title, direction: "inbound" as const, requires, pageId: "catalog", bundle: "Portál",
      contentJson: { type: "requirement" as const, attrs: { key, direction: "inbound", state: "done", party: "x", requires: requires.join(",") }, content: [p(title), p("Výchozí poznámka")] } });
    const ssl = definition("portal.ssl", "SSL certifikát"), sms = definition("portal.sms", "SMS brána", ["portal.ssl"]), done = definition("hotovo", "Už je tu");
    api.catalog.mockResolvedValue({ definitions: [ssl, sms, done].map(({ contentJson: _body, ...light }) => light), warnings: [] });
    api.definitions.mockImplementationOnce(async (_spaceId, keys) => ({ definitions: [ssl, sms, done].filter(item => keys.includes(item.key)), warnings: [] }));
    await open(page(hd(2, "Už je tu", "hotovo"), p()));
    editor.commands.setTextSelection(editor.state.doc.content.size - 1);
    controller.dispatch(catalogCommand());
    await vi.waitFor(() => expect(popover()?.querySelector("[data-requirement-catalog]")).toBeTruthy());
    await vi.waitFor(() => expect(popover().querySelectorAll("[role=option]").length).toBe(1));
    expect(popover().querySelector("[role=option]")!.textContent).toContain("Portál");
    const query = popover().querySelector<HTMLInputElement>("[data-catalog-query]")!;
    query.value = "sms";
    query.dispatchEvent(new Event("input"));
    await flushPromises();
    const options = [...popover().querySelectorAll<HTMLElement>("[role=option]")];
    expect(options.map(option => option.textContent)).toEqual([expect.stringContaining("Portál"), expect.stringContaining("SMS brána")]);
    expect(options[1]!.textContent).toContain("+ SSL certifikát");
    expect(options[1]!.hasAttribute("data-active")).toBe(true);
    query.dispatchEvent(new KeyboardEvent("keydown", { key: " ", bubbles: true, cancelable: true }));
    await flushPromises();
    expect(options[1]!.getAttribute("aria-selected")).toBe("true");
    expect(popover().querySelector("[data-catalog-also]")!.textContent).toContain("Přidá se také: SSL certifikát");
    expect(popover().querySelector("[data-catalog-also]")!.textContent).toContain("SSL certifikát · potřebuje ho: SMS brána");
    expect(popover().querySelector("[data-catalog-summary]")!.textContent).toBe("Vloží se 2 požadavky v 1 modulu, z toho 2 vstupy od zákazníka.");
    expect(api.definitions).not.toHaveBeenCalled();
    query.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    await flushPromises();
    expect(api.definitions).toHaveBeenCalledWith("space-1", ["portal.ssl", "portal.sms"]);
    expect(titles()).toEqual(["Už je tu", "SSL certifikát", "SMS brána"]);
    expect(editor.state.doc.textContent).toContain("Výchozí poznámka");
    // Headings: the id is the catalog key (unique on the page), the state is reset, the dependency line links this page's heading.
    const found = headings();
    expect(found.map(entry => entry.id)).toEqual(["hotovo", "portal.ssl", "portal.sms"]);
    expect(new Set(found.map(entry => entry.id)).size).toBe(3);
    // Beside the requirement heading above them (its level), as /Požadavek places one.
    expect(found[2]).toMatchObject({ state: "planned", level: 2, deps: ["portal.ssl"] });
    expect(found[1]).toMatchObject({ state: "planned", level: 2, deps: [] });
    expect(document.querySelector(".md-context-popover")).toBeNull();
  });

  it("inserts a whole module under its heading, one level below the product it goes under, with the catalog's service (plan 3.4 §6)", async () => {
    const definition = (key: string, title: string, attrs: Record<string, string> = {}) => ({ key, title, titleMarkdown: title, direction: "inbound" as const, requires: [],
      pageId: "catalog", bundle: "NIA", contentJson: { type: "requirement" as const, attrs: { key, direction: "inbound", ...attrs }, content: [p(title)] } });
    api.catalog.mockResolvedValue({ definitions: [definition("nia.cert", "Podpisový certifikát", { system: "NIA" }), definition("nia.contract", "Smlouva")], warnings: [] });
    await open(page(hd(2, "Portál pacienta"), p()));
    editor.commands.setTextSelection(editor.state.doc.content.size - 1);
    controller.dispatch(catalogCommand());
    await vi.waitFor(() => expect(popover()?.querySelectorAll("[data-requirement-catalog] [role=option]").length).toBe(1));
    const query = popover().querySelector<HTMLInputElement>("[data-catalog-query]")!;
    query.value = "nia";
    query.dispatchEvent(new Event("input"));
    await flushPromises();
    query.dispatchEvent(new KeyboardEvent("keydown", { key: " ", bubbles: true, cancelable: true }));
    await flushPromises();
    query.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    await flushPromises();
    const top: string[] = [];
    editor.state.doc.forEach(node => { if (node.type.name === "heading") top.push(`h${node.attrs.level} ${node.textContent}`); });
    expect(top).toEqual(["h2 Portál pacienta", "h3 NIA", "h4 Podpisový certifikát", "h4 Smlouva"]);
    expect(headings().map(entry => [entry.id, entry.state])).toEqual([["nia.cert", "planned"], ["nia.contract", "planned"]]);
    let system: unknown;
    editor.state.doc.forEach(node => { if (node.attrs.headingId === "nia.cert") system = node.attrs.system; });
    expect(system).toBe("NIA");
  });

  it("„Moduly produktu“ (the tree) puts a picked module under the page's product › category, its missing headings made, wherever the caret is (plan 3.5 C)", async () => {
    const definition = (key: string, title: string, bundle: string, requires: string[] = []) => ({ key, title, titleMarkdown: title, direction: "inbound" as const, requires,
      pageId: "catalog", bundle, category: "Rezervace", product: "Portál pacienta", contentJson: { type: "requirement" as const, attrs: { key, direction: "inbound", requires: requires.join(",") }, content: [p(title)] } });
    const web = definition("portal.web", "Webové objednávání", "Webové objednávání (534)"), doctor = definition("portal.doctor", "Objednávání lékařem", "Objednávání lékařem (526)", ["portal.web"]);
    api.catalog.mockResolvedValue({ definitions: [web, doctor].map(({ contentJson: _body, ...light }) => light), warnings: [] });
    api.definitions.mockImplementationOnce(async (_spaceId, keys) => ({ definitions: [web, doctor].filter(item => keys.includes(item.key)), warnings: [] }));
    await open(page(hd(2, "Portál pacienta"), hd(3, "Rezervace"), hd(4, "Webové objednávání (534)"), hd(5, "Webové objednávání", "portal.web"), hd(2, "Kontakty"), p()));
    editor.commands.setTextSelection(editor.state.doc.content.size - 1);
    controller.dispatch(catalogCommand());
    await vi.waitFor(() => expect(popover()?.classList).toContain("is-wide"));
    await vi.waitFor(() => expect(popover().querySelector("[data-catalog-preview]")).toBeTruthy());
    const query = popover().querySelector<HTMLInputElement>("[data-catalog-query]")!;
    query.value = "lékařem";
    query.dispatchEvent(new Event("input"));
    await flushPromises();
    query.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    await flushPromises();
    const top: string[] = [];
    editor.state.doc.forEach(node => { if (node.type.name === "heading") top.push(`h${node.attrs.level} ${node.textContent}${node.attrs.state ? ` [${node.attrs.headingId}]` : ""}`); });
    expect(top).toEqual(["h2 Portál pacienta", "h3 Rezervace", "h4 Webové objednávání (534)", "h5 Webové objednávání [portal.web]",
      "h4 Objednávání lékařem (526)", "h5 Objednávání lékařem [portal.doctor]", "h2 Kontakty"]);
    expect(headings().find(entry => entry.id === "portal.doctor")!.deps).toEqual(["portal.web"]);
  });
});

describe("pickers: type, and Enter takes the first match", () => {
  const typeInto = async (input: HTMLInputElement, text: string) => { input.value = text; input.dispatchEvent(new Event("input")); await flushPromises(); };
  const enter = (input: HTMLInputElement) => input.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));

  it("in the catalog: Enter inserts the first match at once, and with no match shows the empty state and does nothing", async () => {
    const vpn = { key: "vpn", title: "VPN přístup", titleMarkdown: "VPN přístup", direction: "inbound" as const, requires: [], pageId: "c", bundle: "",
      contentJson: { type: "requirement" as const, attrs: { key: "vpn", direction: "inbound" }, content: [p("VPN přístup")] } };
    api.catalog.mockResolvedValue({ definitions: [vpn, { ...vpn, key: "vpn.audit", title: "VPN audit", titleMarkdown: "VPN audit" }], warnings: [] });
    await open(page(p()));
    controller.dispatch(catalogCommand());
    await vi.waitFor(() => expect(popover()?.querySelectorAll("[role=option]").length).toBe(2));
    const query = popover().querySelector<HTMLInputElement>("[data-catalog-query]")!;
    await typeInto(query, "zzz");
    expect(popover().textContent).toContain("V katalogu nic takového není");
    enter(query);
    await flushPromises();
    expect(headings()).toEqual([]);
    await typeInto(query, "vpn");
    enter(query);
    await vi.waitFor(() => expect(titles()).toEqual(["VPN přístup"]));
    expect(api.catalog).toHaveBeenCalledTimes(1);
    expect(api.definitions).toHaveBeenCalledExactlyOnceWith("space-1", ["vpn"]);
    controller.dispatch(catalogCommand());
    await vi.waitFor(() => expect(popover()?.querySelectorAll("[role=option]").length).toBe(2));
    expect(api.catalog).toHaveBeenCalledTimes(1);
  });

  it("in the catalog: an Enter that composes a character or carries Shift or Alt picks nothing", async () => {
    const vpn = { key: "vpn", title: "VPN přístup", titleMarkdown: "VPN přístup", direction: "inbound" as const, requires: [], pageId: "c", bundle: "",
      contentJson: { type: "requirement" as const, attrs: { key: "vpn", direction: "inbound" }, content: [p("VPN přístup")] } };
    api.catalog.mockResolvedValue({ definitions: [vpn], warnings: [] });
    await open(page(p()));
    controller.dispatch(catalogCommand());
    await vi.waitFor(() => expect(popover()?.querySelectorAll("[role=option]").length).toBe(1));
    const query = popover().querySelector<HTMLInputElement>("[data-catalog-query]")!;
    await typeInto(query, "vpn");
    for (const init of [{ isComposing: true }, { altKey: true }, { shiftKey: true }]) query.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true, ...init }));
    await flushPromises();
    expect(headings()).toEqual([]);
  });
});

describe("the catalog", () => {
  const vpn = { key: "vpn", title: "VPN přístup", titleMarkdown: "VPN přístup", direction: "inbound" as const, requires: [], pageId: "c", bundle: "",
    contentJson: { type: "requirement" as const, attrs: { key: "vpn", direction: "inbound" }, content: [p("VPN přístup")] } };

  it("opened from the page menu while the page is scrolled down, comes to the first block in view and inserts there", async () => {
    api.catalog.mockResolvedValue({ definitions: [vpn], warnings: [] });
    await open(page(p("První"), p("Druhý"), p("Třetí"), p("Čtvrtý")));
    editor.commands.setTextSelection(1);
    const children = [...editor.view.dom.children];
    vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) {
      const index = children.indexOf(this);
      return new DOMRect(0, index < 0 ? 0 : index * 100 - 250, 600, index < 0 ? 0 : 100);
    });
    vi.spyOn(editor.view, "coordsAtPos").mockImplementation(pos => {
      const top = editor.state.doc.resolve(Math.min(pos, editor.state.doc.content.size)).index(0) * 100 - 250;
      return { top, bottom: top + 20, left: 0, right: 0 };
    });
    controller.dispatch(controller.actions.toolbar.find(action => action.label === "Moduly produktu")!.command);
    await vi.waitFor(() => expect(popover()?.querySelectorAll("[role=option]").length).toBe(1));
    // A collaborator adds a paragraph above meanwhile: the place in view moves with the document.
    editor.view.dispatch(editor.state.tr.insert(0, editor.schema.nodes.paragraph!.create(null, editor.schema.text("Nový"))));
    for (const key of ["ArrowDown", "Enter"]) popover().querySelector<HTMLInputElement>("[data-catalog-query]")!.dispatchEvent(new KeyboardEvent("keydown", { key, bubbles: true }));
    await vi.waitFor(() => expect(headings()).toHaveLength(1));
    const order = editor.state.doc.content.content.map(child => child.type.name === "heading" ? "VPN" : child.textContent).filter(Boolean);
    expect(order).toEqual(["Nový", "První", "Druhý", "Třetí", "VPN", "Čtvrtý"]);
  });

  it("opened while a requirement heading is the first in view, inserts beside it at the top level, never into it (W4-2)", async () => {
    api.catalog.mockResolvedValue({ definitions: [vpn], warnings: [] });
    await open(page(p("První"), hd(2, "Modul", "m"), p("Poznámka"), p("Další")));
    editor.commands.setTextSelection(1);
    const warn = vi.spyOn(console, "warn");
    const children = [...editor.view.dom.children];
    vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) {
      const index = children.indexOf(this);
      return new DOMRect(0, index < 0 ? 0 : index * 100 - 100, 600, index < 0 ? 0 : 100);
    });
    vi.spyOn(editor.view, "coordsAtPos").mockImplementation(() => ({ top: -80, bottom: -60, left: 0, right: 0 }));
    controller.dispatch(catalogCommand());
    await vi.waitFor(() => expect(popover()?.querySelectorAll("[role=option]").length).toBe(1));
    for (const key of ["ArrowDown", "Enter"]) popover().querySelector<HTMLInputElement>("[data-catalog-query]")!.dispatchEvent(new KeyboardEvent("keydown", { key, bubbles: true }));
    await vi.waitFor(() => expect(headings()).toHaveLength(2));
    // The new heading is a top-level block before the module's heading (its notes stay with the module).
    expect(titles()).toEqual(["VPN přístup", "Modul"]);
    expect(editor.state.doc.child(1).textContent).toBe("VPN přístup");
    expect(warn.mock.calls.flat().join(" ")).not.toContain("TextSelection");
  });

  it("tells why a prerequisite marked „netýká se“ on the page is not added again", async () => {
    const definition = (key: string, title: string, requires: string[] = []) => ({ key, title, titleMarkdown: title, direction: "internal" as const, requires, pageId: "c", bundle: "",
      contentJson: { type: "requirement" as const, attrs: { key, requires: requires.join(",") }, content: [p(title)] } });
    api.catalog.mockResolvedValue({ definitions: [definition("portal", "Portál", ["nis", "notifications"]), definition("nis", "Napojení na NIS"), definition("notifications", "Notifikace")], warnings: [] });
    await open(page(hd(2, "Napojení na NIS", "nis", { state: "not_applicable" }), p()));
    editor.commands.setTextSelection(editor.state.doc.content.size - 1);
    controller.dispatch(catalogCommand());
    await vi.waitFor(() => expect(popover()?.querySelectorAll("[role=option]").length).toBe(3));
    const query = popover().querySelector<HTMLInputElement>("[data-catalog-query]")!;
    query.value = "Portál";
    query.dispatchEvent(new Event("input"));
    await flushPromises();
    query.dispatchEvent(new KeyboardEvent("keydown", { key: " ", bubbles: true, cancelable: true }));
    await vi.waitFor(() => expect(popover().querySelector("[data-catalog-warnings]")).not.toBeNull());
    expect(popover().querySelector("[data-catalog-warnings]")!.textContent).toContain("„nis“ už na stránce je, ale je označený jako nerelevantní; „Portál“ na něm závisí");
    expect(popover().querySelector("[data-catalog-also]")!.textContent).toContain("Notifikace");
    expect(popover().querySelector("[data-catalog-also]")!.textContent).not.toContain("NIS");
  });

  it("draws the first hundred of a big catalog and the rest as the list scrolls to its end; a search looks through all of it", async () => {
    api.catalog.mockResolvedValue({ definitions: Array.from({ length: 400 }, (_, n) => ({ key: `k${n}`, title: `Požadavek ${n}`, titleMarkdown: `Požadavek ${n}`, direction: "internal" as const,
      requires: [], pageId: "c", bundle: "", contentJson: { type: "requirement" as const, content: [p(`Požadavek ${n}`)] } })), warnings: [] });
    await open(page(p()));
    controller.dispatch(catalogCommand());
    const options = () => popover()?.querySelectorAll("[role=option]").length ?? 0;
    await vi.waitFor(() => expect(options()).toBe(100));
    const list = popover().querySelector<HTMLElement>("[data-catalog-list]")!;
    Object.defineProperties(list, { scrollHeight: { value: 3000, configurable: true }, clientHeight: { value: 300, configurable: true } });
    list.scrollTop = 2750;
    list.dispatchEvent(new Event("scroll"));
    await flushPromises();
    expect(options()).toBe(200);
    const query = popover().querySelector<HTMLInputElement>("[data-catalog-query]")!;
    query.value = "Požadavek 399";
    query.dispatchEvent(new Event("input"));
    await flushPromises();
    expect(popover().querySelector("[role=option]")!.textContent).toContain("Požadavek 399");
  });

  it("says kindly when the catalog's dependencies form a cycle", async () => {
    const definition = { key: "a", title: "A", titleMarkdown: "A", direction: "internal" as const, requires: ["b"], pageId: "c", bundle: "", contentJson: { type: "requirement" as const, content: [p("A")] } };
    api.catalog.mockResolvedValue({ definitions: [definition, { ...definition, key: "b", title: "B", requires: ["a"] }], warnings: [] });
    await open(page(p()));
    controller.dispatch(catalogCommand());
    await vi.waitFor(() => expect(popover()?.querySelectorAll("[role=option]").length).toBe(2));
    for (const key of ["ArrowDown", "Enter"]) popover().querySelector<HTMLInputElement>("[data-catalog-query]")!.dispatchEvent(new KeyboardEvent("keydown", { key, bubbles: true }));
    await vi.waitFor(() => expect(popover().querySelector("[role=alert]")?.textContent).toContain("a → b → a"));
    expect(headings()).toEqual([]);
  });
});

describe("the catalog, several at once from the keyboard", () => {
  const item = (key: string, title: string, bundle: string, direction: "inbound" | "internal" = "inbound", requires: string[] = []) => ({ key, title, titleMarkdown: title, direction, requires,
    pageId: "c", bundle, contentJson: { type: "requirement" as const, attrs: { key, direction, requires: requires.join(",") }, content: [p(title)] } });
  const catalog = [item("core.vpn", "VPN přístup", "Core"), item("core.ssl", "SSL certifikát", "Core"), item("core", "Modul Core nasazen", "Core", "internal", ["core.vpn", "core.ssl"]),
    item("nia.cert", "Podpisový certifikát NIA", "NIA"), item("nia", "Modul NIA nasazen", "NIA", "internal", ["nia.cert", "core"])];
  const query = () => popover().querySelector<HTMLInputElement>("[data-catalog-query]")!;
  const key = (name: string, init: KeyboardEventInit = {}) => query().dispatchEvent(new KeyboardEvent("keydown", { key: name, bubbles: true, cancelable: true, ...init }));
  const search = async (text: string) => { query().value = text; query().dispatchEvent(new Event("input")); await flushPromises(); };
  const ticked = () => [...popover().querySelectorAll("[role=option][aria-selected=true]")].map(option => option.querySelector(".catalog-picker-title")!.textContent);
  async function openCatalog() {
    api.catalog.mockResolvedValue({ definitions: catalog, warnings: [] });
    await open(page(p()));
    controller.dispatch(catalogCommand());
    await vi.waitFor(() => expect(popover()?.querySelectorAll("[role=option]").length).toBe(2));
  }

  it("ticks a whole module with Space, shows what it brings for the selection, and Enter inserts it all by module", async () => {
    await openCatalog();
    expect(popover().querySelector("[role=listbox]")!.getAttribute("aria-multiselectable")).toBe("true");
    for (let step = 0; step < 2; step++) key("ArrowDown");
    await flushPromises();
    expect(popover().querySelector("[role=option][data-active]")!.textContent).toContain("NIA");
    key(" ");
    await flushPromises();
    expect(ticked()).toEqual(["NIA"]);
    expect(popover().querySelector("[data-catalog-summary]")!.textContent).toBe("Vloží se 5 požadavků ve 2 modulech, z toho 3 vstupy od zákazníka.");
    expect(popover().querySelector("[data-catalog-also]")!.textContent).toContain("Přidá se také: VPN přístup, SSL certifikát, Modul Core nasazen");
    key("Enter");
    await vi.waitFor(() => expect(titles()).toEqual(["VPN přístup", "SSL certifikát", "Modul Core nasazen", "Podpisový certifikát NIA", "Modul NIA nasazen"]));
    expect(document.querySelector(".md-context-popover")).toBeNull();
    // (the one-after-another motion delays are block-only: not ported)
  });

  it("Ctrl+A ticks every row shown and again none; Shift+Space types a space", async () => {
    await openCatalog();
    await search("certifikát");
    expect([...popover().querySelectorAll("[role=option] .catalog-picker-title")].map(title => title.textContent)).toEqual(["Core", "SSL certifikát", "NIA", "Podpisový certifikát NIA"]);
    key("a", { ctrlKey: true });
    await flushPromises();
    expect(ticked()).toEqual(["SSL certifikát", "Podpisový certifikát NIA"]);
    key("a", { ctrlKey: true });
    await flushPromises();
    expect(ticked()).toEqual([]);
    expect(key(" ", { shiftKey: true })).toBe(true);
  });

  it("Ctrl+Enter inserts and keeps the panel open: the inserted row is on the page, the next pick goes after it; Esc closes", async () => {
    await openCatalog();
    await search("vpn");
    key("Enter", { ctrlKey: true });
    await vi.waitFor(() => expect(titles()).toEqual(["VPN přístup"]));
    expect(popover()).not.toBeNull();
    await search("vpn");
    const vpn = [...popover().querySelectorAll<HTMLElement>("[role=option]")].find(option => option.textContent!.includes("VPN přístup"))!;
    expect(vpn.getAttribute("aria-disabled")).toBe("true");
    expect(vpn.textContent).toContain("Už je na stránce");
    await search("ssl");
    key("Enter");
    await vi.waitFor(() => expect(titles()).toEqual(["VPN přístup", "SSL certifikát"]));
    expect(document.querySelector(".md-context-popover")).toBeNull();
  });

  it("Esc in the search holding text empties it and keeps the panel and its ticks; the next Esc closes the panel (P3)", async () => {
    await openCatalog();
    await search("vpn");
    key(" ");
    await flushPromises();
    expect(ticked()).toEqual(["VPN přístup"]);
    key("Escape");
    await flushPromises();
    expect(query().value).toBe("");
    expect(popover()).not.toBeNull();
    expect(popover().querySelector("[role=option][data-mixed] .catalog-picker-title")!.textContent).toBe("Core");
    key("Escape");
    await flushPromises();
    expect(document.querySelector(".md-context-popover")).toBeNull();
  });
});

describe("requirements from another deployment or a preset", () => {
  const source = (key: string, title: string, requires: string[] = [], bundle = "Portál"): CatalogDefinition => ({ key, title, titleMarkdown: title, direction: "inbound", requires,
    pageId: "page-2", bundle, contentJson: { type: "requirement" as const, attrs: { key, direction: "inbound", state: "done", date: "2026-09-01", party: "IT KBN",
      where: "Trezor", requires: requires.join(",") }, content: [p(title)] } });
  const sources = { presets: vi.fn(async () => [{ pageId: "preset-1", title: "Předvolba Portál", requirements: 2 }]),
    deployments: vi.fn(async () => [{ pageId: PAGE, title: "Tato stránka" }, { pageId: "page-2", title: "KBN — Production" }]),
    pageDefinitions: vi.fn(async () => ({ definitions: [source("portal.vpn", "VPN přístup"), source("portal.sms", "SMS brána", ["portal.vpn"])], warnings: [] as string[] })) };
  beforeEach(() => { Object.assign(api, sources); });
  afterEach(() => { for (const name of Object.keys(sources)) delete (api as Record<string, unknown>)[name]; });

  it("picks the deployment by typing and Enter, then its requirements with what they need, reset for this page", async () => {
    await open(page(p()));
    expect(slash("requirements.fromDeployment")?.label).toBe("Požadavek z nasazení");
    expect(slash("requirements.preset")?.label).toBe("Požadavky ze sady");
    controller.dispatch(slash("requirements.fromDeployment")!.command);
    const search = await vi.waitFor(() => { const found = popover()?.querySelector<HTMLInputElement>("[data-requirement-source] input[type=search]"); expect(found).toBeTruthy(); return found!; });
    const rows = () => [...popover().querySelectorAll("[data-requirement-source] [role=radio]")].map(row => row.textContent);
    expect(rows().map(text => text!.includes("Předvolba Portál") ? "preset" : text!.includes("KBN") ? "kbn" : text)).toEqual(["preset", "kbn"]);
    expect(document.activeElement).toBe(search);
    search.value = "kbn";
    search.dispatchEvent(new Event("input"));
    await flushPromises();
    search.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    await vi.waitFor(() => expect(popover()?.querySelectorAll("[data-requirement-source] [role=option]").length).toBeGreaterThan(0));
    expect(sources.pageDefinitions).toHaveBeenCalledWith("page-2");
    const query = popover().querySelector<HTMLInputElement>("[data-catalog-query]")!;
    query.value = "SMS";
    query.dispatchEvent(new Event("input"));
    await flushPromises();
    query.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    await vi.waitFor(() => expect(titles()).toEqual(["VPN přístup", "SMS brána"]));
    // Each a heading of this page: no state beyond planned, no party, deadline or place (no facts line); the key is its id, the dependency is a link.
    expect(headings().map(entry => [entry.id, entry.state, entry.deps])).toEqual([["portal.vpn", "planned", []], ["portal.sms", "planned", ["portal.vpn"]]]);
    expect(editor.state.doc.textContent).not.toContain("IT KBN");
    expect(editor.state.doc.textContent).not.toContain("Trezor");
    expect(document.querySelector(".md-context-popover")).toBeNull();
  });

  it("Esc in the search of the deployments empties it and keeps the panel; the next Esc closes the panel (P3)", async () => {
    await open(page(p()));
    controller.dispatch(slash("requirements.fromDeployment")!.command);
    const search = await vi.waitFor(() => { const found = popover()?.querySelector<HTMLInputElement>("[data-requirement-source] input[type=search]"); expect(found).toBeTruthy(); return found!; });
    search.value = "kbn";
    search.dispatchEvent(new Event("input"));
    await flushPromises();
    const escape = () => search.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
    escape();
    await flushPromises();
    expect(search.value).toBe("");
    expect(popover()).not.toBeNull();
    escape();
    await flushPromises();
    expect(document.querySelector(".md-context-popover")).toBeNull();
  });

  it("offers the presets alone, and says when the space has none", async () => {
    sources.presets.mockResolvedValueOnce([]);
    await open(page(p()));
    controller.dispatch(slash("requirements.preset")!.command);
    await vi.waitFor(() => expect(popover()?.querySelector("[data-requirement-source-empty]")?.textContent).toContain("nemá sadu"));
    expect(sources.deployments).not.toHaveBeenCalled();
  });
});
