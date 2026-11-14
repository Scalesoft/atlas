import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, reactive } from "vue";
import PageCreationFlow from "./PageCreationFlow.vue";
import { pageTemplateApi } from "../pageTemplateApi";
import { api, post } from "../api";
import { similarPagesApi, type SimilarPage } from "../similarPages";
import { creationSuggestionsApi } from "../creationSuggestions";
import { noAiCapabilities, type AiCapabilities } from "../ai/capabilities";

vi.mock("../similarPages", async original => ({ ...await original<typeof import("../similarPages")>(), similarPagesApi: { find: vi.fn() } }));
vi.mock("../creationSuggestions", async original => ({ ...await original<typeof import("../creationSuggestions")>(), creationSuggestionsApi: { placement: vi.fn(), template: vi.fn() } }));
const caps = reactive<AiCapabilities>(noAiCapabilities());
vi.mock("../composables/useAiCapabilities", () => ({ useAiCapabilities: () => ({ capabilities: { get value() { return caps; } } }) }));
vi.mock("../pageTemplateApi", () => ({ pageTemplateApi: { list: vi.fn() } }));
vi.mock("../api", async original => ({ ...await original<typeof import("../api")>(), api: vi.fn(), post: vi.fn() }));
vi.mock("./DocumentEditor.vue", () => ({ default: defineComponent({ props: ["value"], setup(props) { return () => h("div", { "data-preview": true }, JSON.stringify(props.value)); } }) }));
vi.mock("../pageTemplates", async original => ({ ...await original<typeof import("../pageTemplates")>(), createPageTemplateContent: vi.fn(async () => ({ contentJson: '{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}', markdown: "built in" })) }));

const space = { id: "space-1", name: "Docs", icon: "book", color: "blue", role: "Admin" as const, pinPolicy: "None" as const, pinned: false, pageCount: 0, lastChangedAt: null };
const pages = [{ id: "parent-1", spaceId: "space-1", parentId: null, title: "Parent", version: 1, isPublished: true, canEdit: true }];
const sourceContent = '{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Custom"}]}]}}';
const template = { id: "template-1", spaceId: "space-1", parentId: null, title: "Release", version: 1, isPublished: false };
const sourcePage = { ...template, contentJson: sourceContent, publishedVersion: null, activeFrom: "now", canEdit: true };
const created = { id: "page-1", spaceId: "space-1", parentId: "parent-1", title: "New release", version: 1, isPublished: false, contentJson: sourceContent };
async function flow(asTemplate = false, initialTitle?: string) {
  const wrapper = mount(PageCreationFlow, { props: { space, pages, initialParentId: "parent-1", dark: false, accent: "blue", asTemplate, initialTitle } });
  await flushPromises();
  return wrapper;
}
beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(pageTemplateApi.list).mockResolvedValue([template]);
  vi.mocked(api).mockResolvedValue(sourcePage);
  vi.mocked(post).mockResolvedValue(created);
  vi.mocked(similarPagesApi.find).mockResolvedValue([]);
  vi.mocked(creationSuggestionsApi.placement).mockResolvedValue(null);
  vi.mocked(creationSuggestionsApi.template).mockResolvedValue(null);
  Object.assign(caps, noAiCapabilities());
});
afterEach(() => vi.useRealTimers());

describe("page creation flow", () => {
  // U2-4: with module Projector a deployment page from the catalog is one of the starting contents.
  it("offers a deployment from the catalog only with module Projector", async () => {
    const plain = await flow();
    expect(plain.text()).not.toContain("Nasazení z katalogu");
    plain.unmount();
    const wrapper = mount(PageCreationFlow, { props: { space, pages, initialParentId: "parent-1", dark: false, accent: "blue", projector: true } });
    await flushPromises();
    expect(wrapper.text()).toContain("Nasazení z katalogu");
    wrapper.unmount();
  });
  // M4a: a space template with requirement blocks is offered as a deployment preset; the server resets its requirements on create.
  it("marks a space template with requirements as a deployment preset", async () => {
    vi.mocked(api).mockImplementation(async (path: string) => path.endsWith("/presets") ? [{ pageId: "template-1", title: "Release", requirements: 3 }] as never : sourcePage as never);
    const wrapper = mount(PageCreationFlow, { props: { space, pages, initialParentId: "parent-1", dark: false, accent: "blue", projector: true } });
    await flushPromises();
    expect(vi.mocked(api)).toHaveBeenCalledWith("/projector/spaces/space-1/presets", expect.anything());
    expect(wrapper.get('[data-value="template-1"]').text()).toContain("Sada · 3 požadavky");
    wrapper.unmount();
    // Without the module it is a plain template, nothing asked.
    vi.mocked(api).mockClear();
    const plain = await flow();
    expect(plain.get('[data-value="template-1"]').text()).toContain("Šablona prostoru");
    expect(vi.mocked(api)).not.toHaveBeenCalledWith("/projector/spaces/space-1/presets", expect.anything());
  });
  describe("a new deployment from the catalog (J2)", () => {
    const block = (key: string, title: string) => ({ type: "requirement", attrs: { id: "6f1c1d3e-8a4b-4c55-9d1e-000000000009", key, state: "done" }, content: [{ type: "paragraph", content: [{ type: "text", text: title }] }] });
    const definition = (key: string, title: string, bundle: string, direction: string, requires: string[] = []) => ({ key, title, titleMarkdown: title, direction, requires, pageId: "catalog", bundle, contentJson: block(key, title) });
    const catalog = [definition("core.vpn", "VPN přístup", "Core", "inbound"), definition("core.ssl", "SSL certifikát", "Core", "inbound"),
      definition("core", "Modul Core nasazen", "Core", "internal", ["core.vpn", "core.ssl"]), definition("nia.cert", "Podpisový certifikát", "NIA", "inbound"),
      definition("nia", "Modul NIA nasazen", "NIA", "internal", ["nia.cert", "core"]), definition("bank.client", "BankID klient", "BankID", "inbound")];
    const customer = [{ id: "kbn", spaceId: "space-1", parentId: null, title: "KBN — Portál pacienta", version: 1, isPublished: true, canEdit: true }];
    beforeEach(() => {
      vi.mocked(api).mockImplementation((async (path: string) => {
        if (!path.includes("/projector/")) return sourcePage;
        if (path.includes("bodies=false")) return { definitions: catalog.map(({ contentJson: _block, ...light }) => light), warnings: [] };
        const keys = new URLSearchParams(path.split("?")[1]).getAll("keys");
        return { definitions: catalog.filter(item => keys.includes(item.key)), warnings: [] };
      }) as never);
    });
    const typeInto = async (input: HTMLInputElement, text: string) => { input.value = text; input.dispatchEvent(new Event("input")); await flushPromises(); };
    const key = (input: Element, name: string, init: KeyboardEventInit = {}) => input.dispatchEvent(new KeyboardEvent("keydown", { key: name, bubbles: true, cancelable: true, ...init }));

    it("is one screen from the page menu: the name from the customer, Enter to the modules, Space ticks, a live summary, Enter creates the page by module", async () => {
      const wrapper = mount(PageCreationFlow, { props: { space, pages: customer, initialParentId: "kbn", dark: false, accent: "blue", projector: true, deployment: true }, attachTo: document.body });
      await flushPromises();
      try {
        expect(wrapper.text()).toContain("Nové nasazení");
        expect(wrapper.find("[data-flow-progress], .flow-progress").exists()).toBe(false);
        const name = wrapper.get<HTMLInputElement>('input[name="page-title"]').element;
        expect(name.value).toBe("KBN — nasazení");
        expect(document.activeElement).toBe(name);
        key(name, "Enter");
        await flushPromises();
        expect(post).not.toHaveBeenCalled();
        const query = wrapper.get<HTMLInputElement>("[data-catalog-query]").element;
        expect(document.activeElement).toBe(query);
        expect(wrapper.findAll("[role=option]").map(option => option.find(".catalog-picker-title").text())).toEqual(["Core", "NIA", "BankID"]);
        await typeInto(query, "nia");
        key(query, " ");
        await typeInto(query, "bank");
        key(query, " ");
        await flushPromises();
        expect(wrapper.get("[data-catalog-summary]").text()).toBe("Vloží se 6 požadavků ve 3 modulech, z toho 4 vstupy od zákazníka.");
        key(query, "Enter");
        await flushPromises();
        await flushPromises();
        expect(post).toHaveBeenCalledWith("/pages", expect.objectContaining({ spaceId: "space-1", parentId: "kbn", title: "KBN — nasazení" }));
        const body = vi.mocked(post).mock.calls[0]![1] as { contentJson: string };
        const content = (JSON.parse(body.contentJson).document.content as { type: string; attrs?: { level?: number; headingId?: string; state?: string }; content?: { text: string }[] }[]);
        expect(content.map(node => node.type === "heading" ? (node.attrs!.level === 2 ? `## ${node.content![0]!.text}` : node.attrs!.headingId) : node.type)).toEqual([
          "overview", "## Core", "core.vpn", "core.ssl", "core", "## NIA", "nia.cert", "nia", "## BankID", "bank.client", "paragraph"]);
        expect(content.filter(node => node.type === "heading" && node.attrs!.level === 3).every(node => node.attrs!.state === "planned")).toBe(true);
        expect(body.contentJson).not.toContain('"type":"requirement"');
        expect(wrapper.emitted("created")).toEqual([[created]]);
      } finally { wrapper.unmount(); }
    });

    it("unfolds the module checklist under the name when the chooser picks „Nasazení z katalogu“, with no preview", async () => {
      const wrapper = mount(PageCreationFlow, { props: { space, pages: customer, initialParentId: "kbn", dark: false, accent: "blue", projector: true } });
      await flushPromises();
      try {
        expect(wrapper.find("[data-catalog-picker]").exists()).toBe(false);
        await wrapper.get('[data-value="projector-deployment"]').trigger("click");
        await flushPromises();
        expect(wrapper.find("[data-catalog-picker]").exists()).toBe(true);
        expect(wrapper.find("[data-preview]").exists()).toBe(false);
        expect(wrapper.get("[data-flow-next]").text()).toContain("Vytvořit nasazení");
      } finally { wrapper.unmount(); }
    });
  });
  it("U5: opens at its top with the caret in the name, and Enter there creates the page (not while composing)", async () => {
    const scrolled = document.createElement("div");
    // jsdom has no layout: the scroll position is a plain value here.
    let top = 358;
    Object.defineProperty(scrolled, "scrollTop", { get: () => top, set: (value: number) => { top = value; } });
    document.body.append(scrolled);
    const wrapper = mount(PageCreationFlow, { props: { space, pages, initialParentId: "parent-1", dark: false, accent: "blue" }, attachTo: scrolled });
    await flushPromises();
    try {
      const input = wrapper.get<HTMLInputElement>('input[name="page-title"]');
      expect(document.activeElement).toBe(input.element);
      expect(scrolled.scrollTop).toBe(0);
      await input.setValue("Nová stránka");
      await input.trigger("keydown", { key: "Enter", isComposing: true });
      expect(post).not.toHaveBeenCalled();
      await input.trigger("keydown", { key: "Enter" });
      await flushPromises();
      expect(post).toHaveBeenCalledWith("/pages", expect.objectContaining({ title: "Nová stránka" }));
    } finally { wrapper.unmount(); scrolled.remove(); }
  });
  it("starts with the given initial title in the name field and creates the page under it", async () => {
    const wrapper = await flow(false, "vpn heslo");
    try {
      expect((wrapper.get('input[name="page-title"]').element as HTMLInputElement).value).toBe("vpn heslo");
      await wrapper.get("[data-flow-next]").trigger("click");
      await flushPromises();
      expect(post).toHaveBeenCalledWith("/pages", expect.objectContaining({ spaceId: "space-1", title: "vpn heslo" }));
    } finally { wrapper.unmount(); }
  });
  it("reveals editable details after loading a template and delegates template-page copying to POST /pages", async () => {
    const wrapper = await flow();
    try {
      await wrapper.get('[data-value="template-1"]').trigger("click");
      await flushPromises();
      expect(api).toHaveBeenCalledWith("/pages/template-1");
      expect(wrapper.get("[data-preview]").text()).toContain("Custom");
      await wrapper.get('input[name="page-title"]').setValue("New release");
      expect(wrapper.get('[data-page-id="parent-1"]').attributes("aria-selected")).toBe("true");
      await wrapper.get('[data-value="blank"]').trigger("click");
      await flushPromises();
      await wrapper.get('[data-value="template-1"]').trigger("click");
      await flushPromises();
      expect((wrapper.get('input[name="page-title"]').element as HTMLInputElement).value).toBe("New release");
      await wrapper.get("[data-flow-next]").trigger("click");
      await flushPromises();
      expect(api).toHaveBeenCalledTimes(2);
      expect(post).toHaveBeenCalledWith("/pages", { spaceId: "space-1", parentId: "parent-1", title: "New release", templatePageId: "template-1", contentJson: expect.any(String), markdown: "" });
      expect(wrapper.emitted("created")).toEqual([[created]]);
    } finally { wrapper.unmount(); }
  });
  // P7: the caret goes to the name after the update, not after a timer, so no key typed meanwhile reaches the card.
  it("moves the caret to the name as soon as a template card is clicked, without waiting for a timer", async () => {
    const wrapper = mount(PageCreationFlow, { props: { space, pages, initialParentId: "parent-1", dark: false, accent: "blue" }, attachTo: document.body });
    await flushPromises();
    try {
      vi.useFakeTimers();
      (wrapper.get('[data-value="template-1"]').element as HTMLElement).focus();
      wrapper.get('[data-value="template-1"]').element.dispatchEvent(new MouseEvent("click", { bubbles: true, detail: 1 }));
      await Promise.resolve();
      await Promise.resolve();
      expect(document.activeElement).toBe(wrapper.get('input[name="page-title"]').element);
    } finally { vi.useRealTimers(); wrapper.unmount(); }
  });
  it("creates a template from bundled content through the same page endpoint", async () => {
    const wrapper = await flow(true);
    try {
      expect(wrapper.text()).toContain("Nová šablona");
      await wrapper.get('[data-value="contacts"]').trigger("click");
      await flushPromises();
      await wrapper.get('input[name="page-title"]').setValue("Team contacts");
      await wrapper.get("[data-flow-next]").trigger("click");
      await flushPromises();
      expect(post).toHaveBeenCalledWith("/pages", expect.objectContaining({ spaceId: "space-1", title: "Team contacts", isTemplate: true, markdown: "built in" }));
    } finally { wrapper.unmount(); }
  });
  it("keeps a retained page location out of a template creation request", async () => {
    const wrapper = await flow();
    try {
      expect(wrapper.get('[data-page-id="parent-1"]').attributes("aria-selected")).toBe("true");
      await wrapper.setProps({ asTemplate: true });
      expect(wrapper.text()).toContain("Název šablony");
      expect(wrapper.text()).toContain("nastavení prostoru");
      expect(wrapper.find(".location-picker").exists()).toBe(false);
      await wrapper.get('input[name="page-title"]').setValue("Root template");
      await wrapper.get("[data-flow-next]").trigger("click");
      await flushPromises();
      expect(post).toHaveBeenCalledWith("/pages", expect.objectContaining({ parentId: null, isTemplate: true }));
    } finally { wrapper.unmount(); }
  });
  it("retains input when the backend rejects an unavailable source page", async () => {
    const wrapper = await flow();
    try {
      await wrapper.get('[data-value="template-1"]').trigger("click");
      await flushPromises();
      await wrapper.get('input[name="page-title"]').setValue("New release");
      vi.mocked(post).mockRejectedValueOnce(new Error("Template removed"));
      await wrapper.get("[data-flow-next]").trigger("click");
      await flushPromises();
      expect(wrapper.text()).toContain("Template removed");
      expect(post).toHaveBeenCalledTimes(1);
      expect((wrapper.get('input[name="page-title"]').element as HTMLInputElement).value).toBe("New release");
    } finally { wrapper.unmount(); }
  });
  it("ignores a creation response from a space that has since changed", async () => {
    let complete!: (value: typeof created) => void;
    vi.mocked(post).mockReturnValueOnce(new Promise(resolve => { complete = resolve; }));
    const wrapper = await flow();
    try {
      await wrapper.get('input[name="page-title"]').setValue("Old space page");
      await wrapper.get("[data-flow-next]").trigger("click");
      await wrapper.setProps({ space: { ...space, id: "space-2" } });
      complete(created);
      await flushPromises();
      expect(wrapper.emitted("created")).toBeUndefined();
    } finally { wrapper.unmount(); }
  });
});

const similar = (id: string, extra: Partial<SimilarPage> = {}): SimilarPage => ({ id, spaceId: "space-2", spaceName: "Provoz", title: `Stránka ${id}`, isDraft: false, matchedBy: "title", headingTitle: null, headingId: null, score: 0.5, ...extra });
const typed = "Připojení na VPN z mobilu";
/** The flow is mounted with real timers (it loads templates); the debounce then runs on fake ones. */
async function typeTitle(wrapper: Awaited<ReturnType<typeof flow>>, value: string, wait = 400) {
  vi.useFakeTimers();
  await wrapper.get('input[name="page-title"]').setValue(value);
  await vi.advanceTimersByTimeAsync(wait);
}
const meters = (wrapper: Awaited<ReturnType<typeof flow>>) => wrapper.findAll('[data-similar-page] [role="img"]').map(meter => meter.attributes("aria-label"));

describe("similar pages while creating a page", () => {
  it("suggests similar pages under the title without blocking creation", async () => {
    vi.mocked(similarPagesApi.find).mockResolvedValue([
      similar("1", { title: "Připojení k VPN", spaceName: "IT a infrastruktura", matchedBy: "heading", headingTitle: "Z mobilu", headingId: "z-mobilu", score: 0.86 }),
      similar("2", { title: "VPN pro dodavatele", isDraft: true }),
      similar("3", { title: "Vzdálený přístup", matchedBy: "alias", score: 0.4 }),
    ]);
    const wrapper = await flow();
    try {
      await typeTitle(wrapper, "vpn mob", 1000);
      expect(similarPagesApi.find).not.toHaveBeenCalled();
      await typeTitle(wrapper, typed, 399);
      expect(similarPagesApi.find).not.toHaveBeenCalled();
      await vi.advanceTimersByTimeAsync(1);
      expect(similarPagesApi.find).toHaveBeenCalledWith(typed, "space-1", expect.any(AbortSignal));
      const panel = wrapper.get(".suggestion-chip[data-layout='panel']");
      expect(panel.text()).toContain("Podobné stránky už existují:");
      expect(panel.text()).toContain("Pokračovat se zakládáním můžete i tak.");
      expect(panel.find("[data-ai-glyph]").exists()).toBe(false);
      const rows = wrapper.findAll("[data-similar-page]");
      expect(rows.map(row => row.attributes("data-similar-page"))).toEqual(["1", "2", "3"]);
      expect(rows[0]!.text()).toContain("IT a infrastruktura · sekce „Z mobilu“");
      expect(rows[1]!.text()).toContain("Koncept");
      expect(rows[2]!.text()).toContain("shoda s alternativním názvem");
      // Plain links, so the unfinished draft stays protected by the leave guard (dirty + UnsavedChangesDialog).
      const section = rows[0]!.get("a");
      expect(section.text()).toBe("Otevřít sekci");
      expect(section.attributes("href")).toContain("#/pages/1?heading=z-mobilu");
      expect(rows[1]!.get("a").text()).toBe("Otevřít");
      expect(rows[1]!.get("a").attributes("aria-label")).toBe("Otevřít stránku VPN pro dodavatele");
      expect(rows[1]!.get("a").attributes("target")).toBeUndefined();
      // Next works with the proposal showing.
      vi.useRealTimers();
      expect(wrapper.get("[data-flow-next]").attributes("disabled")).toBeUndefined();
      await wrapper.get("[data-flow-next]").trigger("click");
      await flushPromises();
      expect(post).toHaveBeenCalledWith("/pages", expect.objectContaining({ title: typed }));
    } finally { wrapper.unmount(); }
  });
  // P15 keyboard path: ↓ reaches the existing page, Enter there is the link's (no page created), Esc and ↑ go back, „Přesto založit“ creates.
  it("reaches the first similar page with ↓ and creates only on „Přesto založit“", async () => {
    vi.mocked(similarPagesApi.find).mockResolvedValue([similar("1", { title: "Připojení k VPN" }), similar("2")]);
    const wrapper = mount(PageCreationFlow, { props: { space, pages, initialParentId: "parent-1", dark: false, accent: "blue" }, attachTo: document.body });
    await flushPromises();
    try {
      await typeTitle(wrapper, typed);
      vi.useRealTimers();
      await flushPromises();
      const title = wrapper.get('input[name="page-title"]');
      const links = wrapper.findAll("[data-similar-page] a").map(link => link.element);
      const create = wrapper.get("[data-similar-create]").element;
      // Native events from the focused element, as the keyboard sends them.
      const press = async (key: string) => { document.activeElement!.dispatchEvent(new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true })); await flushPromises(); };
      (title.element as HTMLElement).focus();
      await press("ArrowDown");
      expect(document.activeElement).toBe(links[0]);
      // Enter on the link is the link's own (it opens the page); the flow creates nothing and asks nothing („Neuložené změny“).
      expect(wrapper.emitted("dirty")?.at(-1)).toEqual([true]);
      links[0]!.addEventListener("click", event => event.preventDefault(), { once: true });
      (links[0] as HTMLElement).click();
      expect(wrapper.emitted("dirty")?.at(-1)).toEqual([false]);
      await press("Enter");
      expect(post).not.toHaveBeenCalled();
      await press("ArrowDown");
      expect(document.activeElement).toBe(links[1]);
      await press("ArrowDown");
      expect(document.activeElement).toBe(create);
      await press("Escape");
      expect(document.activeElement).toBe(title.element);
      expect(wrapper.emitted("cancel")).toBeUndefined();
      await press("ArrowDown");
      await press("ArrowUp");
      expect(document.activeElement).toBe(title.element);
      expect(post).not.toHaveBeenCalled();
      (create as HTMLElement).click();
      await flushPromises();
      expect(post).toHaveBeenCalledWith("/pages", expect.objectContaining({ title: typed }));
    } finally { wrapper.unmount(); }
  });
  it("answers a title it has looked up already at once, without the debounce", async () => {
    vi.mocked(similarPagesApi.find).mockResolvedValue([similar("1")]);
    const wrapper = await flow();
    try {
      await typeTitle(wrapper, typed);
      await typeTitle(wrapper, `${typed} doma`);
      expect(similarPagesApi.find).toHaveBeenCalledTimes(2);
      vi.mocked(similarPagesApi.find).mockResolvedValue([]);
      await typeTitle(wrapper, typed, 0);
      expect(wrapper.findAll("[data-similar-page]").map(row => row.attributes("data-similar-page"))).toEqual(["1"]);
      await vi.advanceTimersByTimeAsync(1000);
      expect(similarPagesApi.find).toHaveBeenCalledTimes(2);
    } finally { wrapper.unmount(); }
  });
  it("lets the person create the page while the lookup is still running", async () => {
    vi.mocked(similarPagesApi.find).mockReturnValue(new Promise(() => {}));
    const wrapper = await flow();
    try {
      await typeTitle(wrapper, typed);
      expect(similarPagesApi.find).toHaveBeenCalledTimes(1);
      vi.useRealTimers();
      await wrapper.get("[data-flow-next]").trigger("click");
      await flushPromises();
      expect(post).toHaveBeenCalledTimes(1);
      expect(wrapper.emitted("created")).toEqual([[created]]);
    } finally { wrapper.unmount(); }
  });
  it("shows the matching section and hides the proposal after dismiss", async () => {
    vi.mocked(similarPagesApi.find).mockResolvedValue([similar("1", { matchedBy: "heading", headingTitle: "Z mobilu", headingId: "z-mobilu", score: 0.9 })]);
    const wrapper = await flow();
    try {
      await typeTitle(wrapper, typed);
      expect(wrapper.get("[data-similar-page]").text()).toContain("sekce „Z mobilu“");
      await wrapper.get("[data-suggestion-dismiss]").trigger("click");
      await flushPromises();
      expect(wrapper.find(".suggestion-chip").exists()).toBe(false);
      // A few more characters keep it hidden even if the lookup finds something new.
      vi.mocked(similarPagesApi.find).mockResolvedValue([similar("1"), similar("9")]);
      await typeTitle(wrapper, `${typed} ok`);
      expect(wrapper.find(".suggestion-chip").exists()).toBe(false);
      // A different title brings back only what was not dismissed.
      await typeTitle(wrapper, "Zálohování databáze do cloudu");
      expect(wrapper.findAll("[data-similar-page]").map(row => row.attributes("data-similar-page"))).toEqual(["9"]);
    } finally { wrapper.unmount(); }
  });
  it("does not search for a template", async () => {
    vi.mocked(similarPagesApi.find).mockResolvedValue([similar("1")]);
    const wrapper = await flow(true);
    try {
      await typeTitle(wrapper, typed);
      expect(similarPagesApi.find).not.toHaveBeenCalled();
      expect(wrapper.find(".suggestion-chip").exists()).toBe(false);
    } finally { wrapper.unmount(); }
  });
  it("stays silent on a failed lookup", async () => {
    vi.mocked(similarPagesApi.find).mockRejectedValue(new Error("Server neodpovídá."));
    const wrapper = await flow();
    try {
      await typeTitle(wrapper, typed);
      expect(similarPagesApi.find).toHaveBeenCalledTimes(1);
      expect(wrapper.find(".suggestion-chip").exists()).toBe(false);
      expect(wrapper.find("[role='alert']").exists()).toBe(false);
      vi.mocked(similarPagesApi.find).mockResolvedValue([]);
      await typeTitle(wrapper, `${typed} podruhé`);
      expect(wrapper.find(".suggestion-chip").exists()).toBe(false);
    } finally { wrapper.unmount(); }
  });
  it("draws the match meter from the score with a text alternative and omits it without a score", async () => {
    vi.mocked(similarPagesApi.find).mockResolvedValue([
      similar("1", { score: 0.8649 }),
      similar("2", { score: 0.485 }),
      similar("3", { score: undefined as unknown as number }),
    ]);
    const wrapper = await flow();
    try {
      await typeTitle(wrapper, typed);
      expect(meters(wrapper)).toEqual(["Shoda 86 %", "Shoda 49 %"]);
      expect(wrapper.get('[data-similar-page="1"] [role="img"] > i').attributes("style")).toContain("width: 86%");
      expect(wrapper.get('[data-similar-page="3"]').find("[role='img']").exists()).toBe(false);
    } finally { wrapper.unmount(); }
  });
});

describe("placement and template suggestions while creating a page", () => {
  const twoPages = [
    { id: "parent-1", spaceId: "space-1", parentId: null, title: "Parent", version: 1, isPublished: true, canEdit: true },
    { id: "net", spaceId: "space-1", parentId: null, title: "Síť", version: 1, isPublished: true, canEdit: true },
    { id: "vpn", spaceId: "space-1", parentId: "net", title: "VPN", version: 1, isPublished: true, canEdit: true },
  ];
  const placement = { parentId: "vpn", parentTitle: "VPN", path: [{ id: "net", title: "Síť" }, { id: "vpn", title: "VPN" }] };
  async function flowWith() {
    const wrapper = mount(PageCreationFlow, { props: { space, pages: twoPages, initialParentId: "parent-1", dark: false, accent: "blue" } });
    await flushPromises();
    return wrapper;
  }
  const selectedLocation = (wrapper: Awaited<ReturnType<typeof flow>>) => wrapper.get("[data-location-value]").text();

  it("suggests a location and applies it to the picker without blocking creation", async () => {
    caps.createSuggestions = true;
    vi.mocked(creationSuggestionsApi.placement).mockResolvedValue(placement);
    const wrapper = await flowWith();
    try {
      await typeTitle(wrapper, "vpn", 1000);
      expect(creationSuggestionsApi.placement).not.toHaveBeenCalled();
      await typeTitle(wrapper, typed, 399);
      expect(creationSuggestionsApi.placement).not.toHaveBeenCalled();
      await vi.advanceTimersByTimeAsync(1);
      expect(creationSuggestionsApi.placement).toHaveBeenCalledWith(typed, "space-1", expect.any(AbortSignal));
      const chip = wrapper.get("[data-placement-suggestion]");
      expect(chip.text()).toContain("Návrh: Síť / VPN");
      expect(chip.find("[data-ai-glyph]").exists()).toBe(false);
      // Next is not waiting for it, and nothing changes until Použít.
      expect(wrapper.get("[data-flow-next]").attributes("disabled")).toBeUndefined();
      expect(selectedLocation(wrapper)).toBe("Parent");
      await chip.get("[data-suggestion-apply]").trigger("click");
      await vi.advanceTimersByTimeAsync(0);
      expect(selectedLocation(wrapper)).toBe("Síť / VPN");
      expect(wrapper.get("[data-placement-suggestion]").attributes("data-state")).toBe("applied");
      await wrapper.get("[data-flow-next]").trigger("click");
      await vi.advanceTimersByTimeAsync(0);
      expect(post).toHaveBeenCalledWith("/pages", expect.objectContaining({ parentId: "vpn" }));
    } finally { wrapper.unmount(); }
  });
  it("offers the root of the space as parentId null and keeps a dismissed suggestion away for the same title", async () => {
    caps.createSuggestions = true;
    vi.mocked(creationSuggestionsApi.placement).mockResolvedValue({ parentId: null, parentTitle: null, path: [] });
    const wrapper = await flowWith();
    try {
      await typeTitle(wrapper, typed);
      expect(wrapper.get("[data-placement-suggestion]").text()).toContain("Hlavní úroveň prostoru");
      await wrapper.get("[data-placement-suggestion] [data-suggestion-dismiss]").trigger("click");
      await vi.advanceTimersByTimeAsync(0);
      expect(wrapper.find("[data-placement-suggestion]").exists()).toBe(false);
      await typeTitle(wrapper, `${typed} `);
      expect(wrapper.find("[data-placement-suggestion]").exists()).toBe(false);
    } finally { wrapper.unmount(); }
  });
  it("suggests a template only when the first step left it blank", async () => {
    vi.mocked(creationSuggestionsApi.template).mockResolvedValue({ templateId: "template-1", title: "Release", matchedBy: "dictionary" });
    const wrapper = await flow();
    try {
      await typeTitle(wrapper, "Postup nasazení");
      const chip = wrapper.get("[data-template-suggestion]");
      expect(chip.text()).toContain("Návrh šablony: Release");
      expect(chip.find("[data-ai-glyph]").exists()).toBe(false);
      await chip.get("[data-suggestion-apply]").trigger("click");
      await vi.advanceTimersByTimeAsync(0);
      expect(api).toHaveBeenCalledWith("/pages/template-1");
      await wrapper.get("[data-flow-next]").trigger("click");
      await vi.advanceTimersByTimeAsync(0);
      expect(post).toHaveBeenCalledWith("/pages", expect.objectContaining({ templatePageId: "template-1" }));
    } finally { wrapper.unmount(); }
    // A person who chose content in step 1 is not second-guessed, and a template being created gets nothing.
    vi.mocked(creationSuggestionsApi.template).mockClear();
    vi.useRealTimers();
    const chosen = await flow();
    try {
      await chosen.get('[data-value="contacts"]').trigger("click");
      await flushPromises();
      await typeTitle(chosen, "Postup nasazení");
      expect(creationSuggestionsApi.template).not.toHaveBeenCalled();
      expect(chosen.find("[data-template-suggestion]").exists()).toBe(false);
    } finally { chosen.unmount(); }
    vi.useRealTimers();
    const asTemplate = await flow(true);
    try {
      await typeTitle(asTemplate, "Postup nasazení");
      expect(creationSuggestionsApi.template).not.toHaveBeenCalled();
      expect(creationSuggestionsApi.placement).not.toHaveBeenCalled();
    } finally { asTemplate.unmount(); }
  });
  it("stays silent on 204, 503 or an error and without the capability", async () => {
    // Without the capability the placement is never asked for, whatever the server would answer.
    vi.mocked(creationSuggestionsApi.placement).mockResolvedValue(placement);
    let wrapper = await flowWith();
    try {
      await typeTitle(wrapper, typed);
      expect(creationSuggestionsApi.placement).not.toHaveBeenCalled();
      expect(wrapper.find("[data-placement-suggestion]").exists()).toBe(false);
    } finally { wrapper.unmount(); }
    vi.useRealTimers();
    caps.createSuggestions = true;
    vi.useRealTimers();
    for (const answer of [() => Promise.resolve(null), () => Promise.reject(new Error("ai_unavailable"))]) {
      vi.mocked(creationSuggestionsApi.placement).mockImplementation(answer);
      vi.mocked(creationSuggestionsApi.template).mockImplementation(answer);
      wrapper = await flowWith();
      try {
        await typeTitle(wrapper, typed);
        expect(creationSuggestionsApi.placement).toHaveBeenCalled();
        expect(wrapper.find("[data-placement-suggestion], [data-template-suggestion]").exists()).toBe(false);
        vi.useRealTimers();
        expect(wrapper.get("[data-flow-next]").attributes("disabled")).toBeUndefined();
      } finally { wrapper.unmount(); }
    }
  });
  it("does not offer a parent that is missing from the tree", async () => {
    caps.createSuggestions = true;
    vi.mocked(creationSuggestionsApi.placement).mockResolvedValue({ parentId: "elsewhere", parentTitle: "Jinde", path: [{ id: "elsewhere", title: "Jinde" }] });
    const wrapper = await flowWith();
    try {
      await typeTitle(wrapper, typed);
      expect(wrapper.find("[data-placement-suggestion]").exists()).toBe(false);
    } finally { wrapper.unmount(); }
  });
  it("shows meaning matches after lexical ones without a match section", async () => {
    vi.mocked(similarPagesApi.find).mockResolvedValue([
      similar("1", { title: "Připojení k VPN", score: 0.86 }),
      similar("2", { title: "Vzdálený přístup do sítě", matchedBy: "meaning", score: 0.78 }),
    ]);
    const wrapper = await flow();
    try {
      await typeTitle(wrapper, typed);
      const rows = wrapper.findAll("[data-similar-page]");
      expect(rows.map(row => row.attributes("data-similar-page"))).toEqual(["1", "2"]);
      expect(rows[1]!.text()).toContain("Provoz · podobné tématem");
      expect(rows[1]!.text()).not.toContain("sekce");
      expect(rows[1]!.find("[role='img']").attributes("aria-label")).toBe("Shoda 78 %");
      expect(wrapper.findAll(".suggestion-chip[data-layout='panel']")).toHaveLength(1);
    } finally { wrapper.unmount(); }
  });
});

describe("board creation flow (module Boards)", () => {
  it("names and places a board in one step, without templates or a preview, and reports the created board", async () => {
    vi.mocked(post).mockResolvedValue({ id: "board-1", spaceId: "space-1", title: "Plán", sequence: 0, canEdit: true, items: [] });
    const wrapper = mount(PageCreationFlow, { props: { space, pages, initialParentId: "parent-1", dark: false, accent: "blue", asBoard: true } });
    await flushPromises();
    try {
      expect(wrapper.text()).toContain("Nová tabule");
      expect(pageTemplateApi.list).not.toHaveBeenCalled();
      expect(wrapper.find('[data-value="blank"]').exists()).toBe(false);
      expect(wrapper.get('[data-page-id="parent-1"]').attributes("aria-selected")).toBe("true");
      expect(wrapper.get("[data-flow-next]").text()).toContain("Vytvořit tabuli");
      await wrapper.get('input[name="page-title"]').setValue("Plán");
      await wrapper.get("[data-flow-next]").trigger("click");
      await flushPromises();
      expect(post).toHaveBeenCalledWith("/spaces/space-1/boards", { title: "Plán", parentPageId: "parent-1" });
      expect(wrapper.emitted("createdBoard")).toEqual([["board-1"]]);
      expect(wrapper.emitted("created")).toBeUndefined();
      expect(similarPagesApi.find).not.toHaveBeenCalled();
    } finally { wrapper.unmount(); }
  });
});
