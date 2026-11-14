import { reactive } from "vue";
import { describe, it, expect, vi } from "vitest";
import { flushPromises, mount, shallowMount } from "@vue/test-utils";
import type { MarkdownCollaborationAdapter } from "@atlas/markdown/core";
import { emptyDocument, readDocument } from "../document";
import { joinPageCollaboration } from "../collaboration";
import { atlasContentPlugins } from "../documentWidgets";
import DocumentEditor, { codeBlockHighlightingReady } from "./DocumentEditor.vue";
import { insertAttachment } from "@atlas/markdown/plugins/attachments";
import { insertContact, insertInlineContact } from "@atlas/markdown/plugins/contacts";
import { pageReferenceNode } from "@atlas/markdown/plugins/pages";
import type { RevealTarget } from "../editor/revealTarget";
vi.mock("../collaboration", async (original) => ({ ...await original<object>(), joinPageCollaboration: vi.fn() }));
vi.mock("../documentWidgets", async (original) => {
  const actual = await original<typeof import("../documentWidgets")>();
  return { ...actual, atlasContentPlugins: vi.fn(actual.atlasContentPlugins) };
});

describe("block insertion and OpenAPI policy", () => {
  const blockInsertion = { showBlockAddButton: true, showBlockInserter: true };
  it("gives the local and the collaborative editor the same inserter and the edit policy", async () => {
    const props = { value: emptyDocument(), readonly: false, dark: false, accent: "blue" as const, spaceId: "space",
      pageId: "page", identity: { id: "user", displayName: "Jana Nováková" } };
    const local = shallowMount(DocumentEditor, { props });
    vi.mocked(joinPageCollaboration).mockResolvedValueOnce({
      lease: { documentId: "page", generation: "session", currentVersion: 3 }, adapter: {} as MarkdownCollaborationAdapter,
    });
    const collaborative = shallowMount(DocumentEditor, { props: { ...props, collaborationAllowed: true } });
    try {
      // The editors wait for the code highlighting and the on-demand Markdown dialect, dynamic imports flushPromises does not cover.
      await codeBlockHighlightingReady;
      const localEditor = await vi.waitFor(() => local.getComponent({ name: "MarkdownEditor" }));
      const sharedEditor = await vi.waitFor(() => collaborative.getComponent({ name: "MarkdownEditor" }));
      expect(localEditor.props("collaboration")).toBeUndefined();
      expect(sharedEditor.props("collaboration")).toBeDefined();
      for (const editor of [localEditor, sharedEditor]) {
        expect(editor.props()).toMatchObject(blockInsertion);
        expect((editor.props("plugins") as { id: string }[]).map(plugin => plugin.id)).toContain("openapi");
      }
      // Each instance hands the page's own detail workspace to the OpenAPI plugin.
      const contexts = vi.mocked(atlasContentPlugins).mock.calls.map(([context]) => context);
      expect(contexts).toHaveLength(2);
      for (const context of contexts) expect(context).toMatchObject({ kind: "edit", canWrite: expect.any(Function), openDetail: expect.any(Function), uploaderName: "Jana Nováková" });
      expect(contexts[0]).not.toHaveProperty("openDetail", (contexts[1] as { openDetail?: unknown }).openDetail);
    } finally { local.unmount(); collaborative.unmount(); vi.mocked(atlasContentPlugins).mockClear(); }
  });
  it("3.6-0: switches the right to write through canWrite() without building new content plugins", async () => {
    vi.mocked(atlasContentPlugins).mockClear();
    const wrapper = shallowMount(DocumentEditor, { props: { value: emptyDocument(), readonly: false, dark: false, accent: "blue", spaceId: "space" } });
    try {
      const context = vi.mocked(atlasContentPlugins).mock.calls[0]![0] as { canWrite: () => boolean };
      expect(context.canWrite()).toBe(true);
      await wrapper.setProps({ readonly: true });
      expect(context.canWrite()).toBe(false);
      await wrapper.setProps({ readonly: false });
      expect(context.canWrite()).toBe(true);
      // Switching the right builds nothing.
      const built = vi.mocked(atlasContentPlugins).mock.calls.length;
      await wrapper.setProps({ readonly: true });
      await wrapper.setProps({ readonly: false });
      expect(vi.mocked(atlasContentPlugins).mock.calls.length).toBe(built);
    } finally { wrapper.unmount(); vi.mocked(atlasContentPlugins).mockClear(); }
  });
  // 3.6 perf: GET /api/projector/pages/{id}/requirements answered 404 on every open of a wiki without the module (its routes are not mapped).
  it("offers the requirement blocks no reads of the Projector's routes while the module is off", () => {
    vi.mocked(atlasContentPlugins).mockClear();
    const wrapper = shallowMount(DocumentEditor, { props: { value: emptyDocument(), readonly: true, dark: false, accent: "blue", spaceId: "space", pageId: "page" } });
    try {
      const { api } = (vi.mocked(atlasContentPlugins).mock.calls[0]![0] as { projector: { api: Record<string, unknown> } }).projector;
      expect(api.pageRequirements).toBeUndefined();
      expect(api.prerequisites).toBeUndefined();
      expect(api.catalog).toBeTypeOf("function");
    } finally { wrapper.unmount(); vi.mocked(atlasContentPlugins).mockClear(); }
  });
  it("shows no block inserter while reading", async () => {
    const wrapper = mount(DocumentEditor, { props: { value: emptyDocument(), readonly: true, dark: false, accent: "blue", spaceId: "test" }, attachTo: document.body });
    try {
      await vi.waitFor(() => expect(wrapper.emitted("ready")).toBeTruthy());
      // The one editor is read-only: no editing controls appear.
      expect(wrapper.getComponent({ name: "MarkdownEditor" }).props("readonly")).toBe(true);
      await wrapper.get(".ProseMirror").trigger("pointermove");
      expect(document.querySelector(".md-block-inserter")).toBeNull();
      // The block handle stays mounted (editability is a flag, contract J1.2) and hides itself while the editor is read-only.
      const handle = document.querySelector<HTMLElement>(".dh-add")?.closest<HTMLElement>(".dh-wrap");
      expect(handle?.style.visibility ?? "hidden").toBe("hidden");
    } finally { wrapper.unmount(); }
  });
});

describe("the page's reveal wave (F7)", () => {
  it("plays once as the page draws, on its first eight blocks one after another, and is gone after the last rose: no redraw or edit plays it again", async () => {
    const value: ReturnType<typeof emptyDocument> = { ...emptyDocument(), document: { type: "doc", content: Array.from({ length: 12 }, (_, index) =>
      ({ type: "paragraph", content: [{ type: "text", text: `Řádek ${index}` }] })) } };
    const played: { block: Element; delay: number; finish: () => void }[] = [];
    // jsdom has no Web Animations: each one is recorded and finished by the test.
    Object.defineProperty(Element.prototype, "animate", { configurable: true, value(this: Element, _frames: Keyframe[], options: KeyframeAnimationOptions) {
      let finish!: () => void;
      const finished = new Promise<Animation>(resolve => { finish = () => resolve(animation); });
      const animation = { finished } as unknown as Animation;
      played.push({ block: this, delay: Number(options.delay ?? 0), finish });
      return animation;
    } });
    const animate = { mockRestore: () => { delete (Element.prototype as Partial<Element>).animate; } };
    const wrapper = mount(DocumentEditor, { props: { value, readonly: false, dark: false, accent: "blue", spaceId: "test" }, attachTo: document.body });
    try {
      await vi.waitFor(() => expect(played).toHaveLength(8));
      const surface = wrapper.get(".document-editor-surface");
      expect(surface.attributes("data-atlas-reveal")).toBe("true");
      const blocks = wrapper.findAll(".ProseMirror > p").map(block => block.element);
      expect(played.map(entry => blocks.indexOf(entry.block))).toEqual([0, 1, 2, 3, 4, 5, 6, 7]);
      expect(played[7]!.delay).toBeGreaterThan(played[1]!.delay);
      // Another block's rise (the first one's) ends nothing; the eighth's ends the wave.
      played[0]!.finish(); await flushPromises();
      expect(surface.attributes("data-atlas-reveal")).toBe("true");
      played[7]!.finish(); await flushPromises();
      expect(surface.attributes("data-atlas-reveal")).toBeUndefined();
      expect(played).toHaveLength(8);
    } finally { animate.mockRestore(); wrapper.unmount(); }
  });
  it("does not play for a page that takes its arrival's place: its blocks stand where they draw", async () => {
    const wrapper = mount(DocumentEditor, { props: { value: emptyDocument(), readonly: false, dark: false, accent: "blue", spaceId: "test", arrived: true }, attachTo: document.body });
    try {
      await vi.waitFor(() => expect(wrapper.find(".ProseMirror").exists()).toBe(true));
      expect(wrapper.get(".document-editor-surface").attributes("data-atlas-reveal")).toBeUndefined();
    } finally { wrapper.unmount(); }
  });
});

describe("heading levels", () => {
  it.each([true, false])("renders H1–H6 as six distinct elements (readonly %s)", async (readonly) => {
    const value: ReturnType<typeof emptyDocument> = { ...emptyDocument(), document: { type: "doc", content: [1, 2, 3, 4, 5, 6].map(level =>
      ({ type: "heading", attrs: { level }, content: [{ type: "text", text: `Úroveň ${level}` }] })) } };
    const wrapper = mount(DocumentEditor, { props: { value, readonly, dark: false, accent: "blue", spaceId: "test" }, attachTo: document.body });
    try {
      await vi.waitFor(() => expect(wrapper.find("h6").exists(), wrapper.html()).toBe(true), { timeout: 3000 });
      expect(wrapper.findAll(".ProseMirror :is(h1, h2, h3, h4, h5, h6)").map(heading => `${heading.element.tagName}:${heading.text()}`))
        .toEqual([1, 2, 3, 4, 5, 6].map(level => `H${level}:Úroveň ${level}`));
    } finally { wrapper.unmount(); }
  });
});

describe("document widgets", () => {
  it.each([['card', insertContact], ['inline', insertInlineContact]] as const)("inserts a %s contact and reopens its saved data in readonly mode", async (_kind, insert) => {
    const props = { value: emptyDocument(), readonly: false, dark: false, accent: "blue" as const, spaceId: "test" };
    const wrapper = mount(DocumentEditor, { props, attachTo: document.body });
    let reader: ReturnType<typeof mount> | undefined;
    try {
      await vi.waitFor(() => expect(wrapper.emitted("ready")).toBeTruthy());
      const controller = (wrapper.findComponent({ name: "MarkdownEditor" }).vm as unknown as {
        controller: import("@atlas/markdown/core").MarkdownEditorController<"rich">;
      }).controller;
      // `username` (editor 0.8.0+) comes from the people directory (editorDirectories.ts) and is stored with the contact.
      expect(controller.dispatch(insert({ name: "Jana Nováková", username: "jnovakova", emails: ["jana@example.cz"], phones: ["+420 555 010 101"] }))).toBe(true);
      const saved = readDocument(wrapper.vm.capture().contentJson);
      expect(JSON.stringify(saved.document)).toContain("\"username\":\"jnovakova\"");
      reader = mount(DocumentEditor, { props: { ...props, value: saved, readonly: true }, attachTo: document.body });
      await vi.waitFor(() => expect(reader!.text()).toContain("Jana Nováková"));
      // The card shows the login labelled; the inline chip shows the name only.
      if (_kind === "card") expect(reader.text()).toContain("jnovakova");
      expect(reader.get('.ProseMirror').attributes('contenteditable')).toBe('false');
      expect(readDocument((reader.vm as unknown as { capture(): { contentJson: string } }).capture().contentJson)).toEqual(saved);
    } finally { wrapper.unmount(); reader?.unmount(); }
  });

});

describe("documents the server validator stores", () => {
  // Atlas 3.6 has no disclosure block (every heading folds): a stored one, of either shape, opens in both modes as a heading and its body.
  it.each([
    ["an attribute title, editable", { title: "Podrobnosti" }, [], false],
    ["a title node, read only", { title: null }, [{ type: "disclosureTitle", content: [{ type: "text", text: "Podrobnosti" }] }], true],
  ] as const)("opens a stored disclosure with %s as a heading and its body", async (_, attrs, title, readonly) => {
    const value = { ...emptyDocument(), document: { type: "doc", content: [
      { type: "disclosure", attrs, content: [...title, { type: "paragraph", content: [{ type: "text", text: "Skryté" }] }] },
    ] } } as ReturnType<typeof emptyDocument>;
    const wrapper = mount(DocumentEditor, { props: { value, readonly, dark: false, accent: "blue", spaceId: "test" }, attachTo: document.body });
    try {
      await vi.waitFor(() => expect(wrapper.find(".ProseMirror h3").exists(), wrapper.html()).toBe(true), { timeout: 3000 });
      expect(wrapper.get(".ProseMirror h3").text()).toBe("Podrobnosti");
      expect(wrapper.text()).toContain("Skryté");
      expect(wrapper.get(".ProseMirror").attributes("contenteditable")).toBe(readonly ? "false" : "true");
      expect(readDocument(wrapper.vm.capture().contentJson).document).toMatchObject({ type: "doc", content: [
        { type: "heading", attrs: { level: 3 }, content: [{ type: "text", text: "Podrobnosti" }] }, { type: "paragraph", content: [{ type: "text", text: "Skryté" }] }] });
    } finally { wrapper.unmount(); }
  });
});

describe("editor appearance boundary", () => {
  it("leaves the page outline to the page dock and keeps the reading surface on theme changes", async () => {
    const value: ReturnType<typeof emptyDocument> = { ...emptyDocument(), plugins: [{ id: "heading-anchors", version: 1 }, { id: "pages", version: 1 }], document: { type: "doc", content: [

      { type: "heading", attrs: { level: 1, headingId: "intro" }, content: [{ type: "text", text: "Úvod" }] },
      { type: "paragraph", content: [{ type: "text", text: "První odstavec." }] },
      { type: "heading", attrs: { level: 2, headingId: "installation" }, content: [{ type: "text", text: "Instalace" }] },
      { type: "paragraph", content: [{ type: "text", text: "Postup instalace." }] },
    ] } };
    const wrapper = mount(DocumentEditor, { props: { value, readonly: true, dark: false, accent: "blue", spaceId: "test", pageId: "page" }, attachTo: document.body });
    try {
      await vi.waitFor(() => expect(wrapper.find('h2').exists(), wrapper.html()).toBe(true), { timeout: 3000 });
      // Refresh 2.5: „Na této stránce“ is the dock's Obsah (PageOutline); the editor draws no rail or trigger of its own.
      expect(wrapper.find('[aria-label="Zobrazit obsah"]').exists()).toBe(false);
      expect(wrapper.find('.md-outline-rail').exists()).toBe(false);
      expect(wrapper.get('h2').attributes('data-heading-id')).toBe('installation');
      const surface = wrapper.get('.ProseMirror').element;
      const before = wrapper.vm.capture();
      await wrapper.setProps({ dark: true, accent: 'green' });
      await flushPromises();
      expect(wrapper.get('.ProseMirror').element).toBe(surface);
      expect(wrapper.get('.ProseMirror').attributes('contenteditable')).toBe('false');
      expect(wrapper.vm.capture()).toEqual(before);
      expect(wrapper.emitted('change')).toBeUndefined();
    } finally { wrapper.unmount(); vi.restoreAllMocks(); }
  });
  it("keeps the existing controller, content and undo history when appearance changes", async () => {
    const wrapper = mount(DocumentEditor, {
      props: {
        value: emptyDocument(),
        readonly: false,
        dark: false,
        accent: "blue",
        spaceId: "test",
      },
      attachTo: document.body,
    });
    const instance = wrapper.findComponent({ name: "MarkdownEditor" })
      .vm as unknown as {
      controller: ReturnType<
        typeof import("@atlas/markdown/core").createMarkdownEditorController
      >;
    };
    const controller = instance.controller;
    expect(
      controller.dispatch(insertAttachment({ version: 1, name: "Theme test.pdf", url: "/files/theme-test.pdf", size: 42, mimeType: "application/pdf" })),
    ).toBe(true);
    const before = JSON.stringify(controller.getValue());
    await wrapper.setProps({ dark: true, accent: "green" });
    expect(
      (
        wrapper.findComponent({ name: "MarkdownEditor" })
          .vm as unknown as typeof instance
      ).controller,
    ).toBe(controller);
    expect(JSON.stringify(controller.getValue())).toBe(before);
    expect(controller.query({ type: "undo" }).enabled).toBe(true);
    expect(controller.dispatch({ type: "undo" })).toBe(true);
    expect(JSON.stringify(controller.getValue())).not.toBe(before);
    wrapper.unmount();
  });
  it("registers the editor-tools plugin so code blocks get a language label and a copy control", async () => {
    const wrapper = mount(DocumentEditor, {
      props: { value: emptyDocument(), readonly: false, dark: false, accent: "blue", spaceId: "test" },
      attachTo: document.body,
    });
    const plugins = wrapper.findComponent({ name: "MarkdownEditor" }).props("plugins") as { id: string }[];
    expect(plugins.some(plugin => plugin.id === "editor-tools")).toBe(true);
    wrapper.unmount();
  });
  it("hides the editor's own plugin toolbar and hands the plugin actions to the host", async () => {
    const wrapper = mount(DocumentEditor, { props: { value: emptyDocument(), readonly: false, dark: false, accent: "blue", spaceId: "test" }, attachTo: document.body });
    try {
      await vi.waitFor(() => expect(wrapper.emitted("pluginActions")).toBeTruthy(), { timeout: 3000 });
      expect(wrapper.find(".md-plugin-toolbar").exists()).toBe(false);
      const actions = wrapper.emitted("pluginActions")!.at(-1)![0] as { label: string; run: () => boolean }[];
      expect(actions.map((action) => action.label)).toEqual(expect.arrayContaining(["Příloha", "Hledat a nahradit", "Zobrazit neviditelné znaky", "Kontakty", "Kontakt v textu", "Graf", "Diagram", "Matematický vzorec", "Vzorec v textu", "Obsah dokumentu", "API", "Skrýt obsah"]));
      expect(actions.every((action) => typeof action.run === "function")).toBe(true);
      // The drag handle keeps its "+" button beside the between-blocks inserter while editing.
      expect(document.querySelector(".dh-grip")).not.toBeNull();
      expect(document.querySelector(".dh-add")).not.toBeNull();
    } finally { wrapper.unmount(); }
  });
});

describe("reveal (publication check, package E)", () => {
  it("reveal selects a page reference by target id and a heading by id", async () => {
    const target = "0a1b2c3d-0000-0000-0000-000000000002";
    const value = readDocument(JSON.stringify({ format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 1 }, { id: "pages", version: 1 }], document: { type: "doc", content: [
      { type: "heading", attrs: { level: 2, headingId: "pristup" }, content: [{ type: "text", text: "Přístup" }] },
      { type: "paragraph", content: [{ type: "text", text: "Viz " }, pageReferenceNode({ id: target, title: "Staré VPN", url: `#/pages/${target}` })] },
      { type: "paragraph", content: [{ type: "text", text: "Účet admin, heslo Srv#2026!rack" }] },
      { type: "paragraph", content: [{ type: "text", text: "Odkaz", marks: [{ type: "link", attrs: { href: "#/pages/0A1B2C3D-0000-0000-0000-000000000003?heading=x" } }] }] },
    ] } }));
    const wrapper = mount(DocumentEditor, { props: { value, readonly: false, dark: false, accent: "blue", spaceId: "test", pageId: "page-1" }, attachTo: document.body });
    try {
      await vi.waitFor(() => expect(wrapper.emitted("ready"), wrapper.text()).toBeTruthy());
      const controller = (wrapper.findComponent({ name: "MarkdownEditor" }).vm as unknown as {
        controller: import("@atlas/markdown/core").MarkdownEditorController<"rich">;
      }).controller;
      const reveal = (wrapper.vm as unknown as { reveal(target: RevealTarget): boolean }).reveal;
      const before = JSON.stringify(controller.getValue());
      const editor = () => (document.querySelector(".ProseMirror") as unknown as { editor: import("@tiptap/core").Editor }).editor;
      const selection = () => editor().state.selection as import("@tiptap/pm/state").Selection & { node?: import("@tiptap/pm/model").Node };
      const selectedText = () => editor().state.doc.textBetween(selection().from, selection().to);
      expect(reveal({ kind: "page", pageId: target.toUpperCase() })).toBe(true);
      expect(selection().node?.type.name).toBe("pageReference");
      expect(selection().node?.attrs.id).toBe(target);
      // By anchor id, even when the path no longer leads there.
      expect(reveal({ kind: "heading", id: "pristup", path: [9] })).toBe(true);
      expect(selection().$from.parent.textContent).toBe("Přístup");
      // A text range: offsets inside the block's inline content.
      expect(reveal({ kind: "text", path: [2], from: 18, to: 31 })).toBe(true);
      expect(selectedText()).toBe("Srv#2026!rack");
      // A link mark whose href leads to the page.
      expect(reveal({ kind: "page", pageId: "0a1b2c3d-0000-0000-0000-000000000003" })).toBe(true);
      expect(selectedText()).toBe("Odkaz");
      // A place that is gone is not an error: nothing happens.
      expect(reveal({ kind: "text", path: [7], from: 0, to: 3 })).toBe(false);
      expect(reveal({ kind: "page", pageId: "missing" })).toBe(false);
      // Revealing never changes the document.
      expect(JSON.stringify(controller.getValue())).toBe(before);
      expect(wrapper.emitted("change")).toBeFalsy();
    } finally { wrapper.unmount(); }
  });

  it("reveals a target that is a reactive object, as the findings of the panel are (structuredClone refuses a proxy)", async () => {
    const value = readDocument(JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [
      { type: "paragraph", content: [{ type: "text", text: "Účet admin, heslo Srv#2026!rack" }] },
    ] } }));
    const wrapper = mount(DocumentEditor, { props: { value, readonly: false, dark: false, accent: "blue", spaceId: "test", pageId: "page-1" }, attachTo: document.body });
    try {
      await vi.waitFor(() => expect(wrapper.emitted("ready"), wrapper.text()).toBeTruthy());
      const reveal = (wrapper.vm as unknown as { reveal(target: RevealTarget): boolean }).reveal;
      const finding = reactive({ target: { kind: "text" as const, path: [0], from: 18, to: 31 } });
      expect(reveal(finding.target)).toBe(true);
      const editor = (document.querySelector(".ProseMirror") as unknown as { editor: import("@tiptap/core").Editor }).editor;
      expect(editor.state.doc.textBetween(editor.state.selection.from, editor.state.selection.to)).toBe("Srv#2026!rack");
    } finally { wrapper.unmount(); }
  });
});

// W2-4: Upravit after reading half the page puts the caret where the reader is, not at the start of the document (which the
// editor does not scroll to).
describe("focus where the reader is", () => {
  it("puts the caret into the first block below the toolbar", async () => {
    const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
    const value = { ...emptyDocument(), document: { type: "doc", content: [paragraph("První"), paragraph("Druhý"), paragraph("Třetí")] } };
    const wrapper = mount(DocumentEditor, { props: { value, readonly: false, dark: false, accent: "blue", spaceId: "space" }, attachTo: document.body });
    const rect = Element.prototype.getBoundingClientRect;
    try {
      await vi.waitFor(() => expect(document.querySelector(".ProseMirror[contenteditable=true]")).not.toBeNull());
      await flushPromises();
      // jsdom has no layout: the first two paragraphs are scrolled above the top, the third is the first one on screen.
      let measured = 0;
      Element.prototype.getBoundingClientRect = function (this: Element) {
        measured++;
        const index = this.parentElement?.classList.contains("ProseMirror") ? [...this.parentElement.children].indexOf(this) : -1;
        return (index < 0 ? rect.call(this) : { top: index * 300 - 500, bottom: index * 300 - 480, height: 20 }) as DOMRect;
      };
      const exposed = (wrapper.vm.$ as unknown as { exposed: { focus: (at?: "visible") => boolean } }).exposed;
      expect(exposed.focus("visible")).toBe(true);
      // Focused at once, measured only after the next paint (perf guard: a measure now forces a layout of the page just opened).
      expect(document.activeElement).toBe(document.querySelector(".ProseMirror"));
      expect(measured).toBe(0);
      const editor = (document.querySelector(".ProseMirror") as unknown as { editor: import("@tiptap/core").Editor }).editor;
      await vi.waitFor(() => expect(editor.state.selection.$from.parent.textContent).toBe("Třetí"));
    } finally { Element.prototype.getBoundingClientRect = rect; wrapper.unmount(); }
  });

  // C3-11: a first visible block without inline content of its own (a requirement, a quote) warned "TextSelection endpoint not
  // pointing into a node with inline content" and left the selection invalid.
  it("puts the caret into the text inside a block that holds blocks", async () => {
    const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
    const requirement = { type: "requirement", attrs: { id: "0b6f7c1e-5d2a-4f3b-9c8d-1e2f3a4b5c6d", key: "ssl", direction: "internal", state: "planned", date: "", party: "", where: "", requires: "" }, content: [paragraph("Citace")] };
    const value = { ...emptyDocument(), plugins: [{ id: "atlas-requirements", version: 1 }], document: { type: "doc", content: [requirement, paragraph("Další")] } };
    const wrapper = mount(DocumentEditor, { props: { value, readonly: false, dark: false, accent: "blue", spaceId: "space" }, attachTo: document.body });
    const warn = vi.spyOn(console, "warn");
    const rect = Element.prototype.getBoundingClientRect;
    try {
      await vi.waitFor(() => expect(document.querySelector(".ProseMirror[contenteditable=true]"), wrapper.text()).not.toBeNull());
      await flushPromises();
      // jsdom has no layout: the requirement is the first block on screen.
      Element.prototype.getBoundingClientRect = function (this: Element) {
        return (this.parentElement?.classList.contains("ProseMirror") ? { top: 10, bottom: 80, height: 70 } : rect.call(this)) as DOMRect;
      };
      warn.mockClear();
      const exposed = (wrapper.vm.$ as unknown as { exposed: { focus: (at?: "visible") => boolean } }).exposed;
      expect(exposed.focus("visible")).toBe(true);
      const editor = (document.querySelector(".ProseMirror") as unknown as { editor: import("@tiptap/core").Editor }).editor;
      await vi.waitFor(() => expect(editor.state.selection.$from.parent.textContent).toBe("Citace"));
      expect(editor.state.selection.$from.parent.type.name).toBe("paragraph");
      expect(warn.mock.calls.flat().join(" ")).not.toContain("TextSelection endpoint");
    } finally { Element.prototype.getBoundingClientRect = rect; warn.mockRestore(); wrapper.unmount(); }
  });

  // SE3-5: Esc from the palette gives the focus back to the editor; a key typed at once went to where the browser's caret was (the
  // start of the line), because ProseMirror wrote its own selection back only 20 ms after the focus.
  it("has its own caret in the page the moment focus comes back without a pointer", async () => {
    const value = { ...emptyDocument(), document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Alfa beta gama" }] }] } };
    const wrapper = mount(DocumentEditor, { props: { value, readonly: false, dark: false, accent: "blue", spaceId: "space" }, attachTo: document.body });
    const outside = document.body.appendChild(document.createElement("input"));
    try {
      await vi.waitFor(() => expect(document.querySelector(".ProseMirror[contenteditable=true]")).not.toBeNull());
      await flushPromises();
      const dom = document.querySelector(".ProseMirror") as HTMLElement & { editor: import("@tiptap/core").Editor };
      dom.editor.commands.setTextSelection(13);
      outside.focus();
      document.getSelection()!.collapse(document.body, 0);
      dom.focus();
      const selection = document.getSelection()!;
      expect(selection.anchorNode?.textContent).toBe("Alfa beta gama");
      expect(selection.anchorOffset).toBe(12);
    } finally { outside.remove(); wrapper.unmount(); }
  });
});

describe("one editor for reading and writing", () => {
  // 3.6-G N1: a link to a wiki page opens in this window (a new tab started the whole app cold); Ctrl/Cmd keeps the browser's new tab.
  it("follows a link to a wiki page in this window on a plain click and leaves a modified click to the browser", async () => {
    const value = readDocument(JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph", content: [
      { type: "text", text: "Viz", marks: [{ type: "link", attrs: { href: "#/pages/11111111-2222-4333-8444-555555555555" } }] }] }] } }));
    const wrapper = mount(DocumentEditor, { props: { value, readonly: true, dark: false, accent: "blue", spaceId: "test" }, attachTo: document.body });
    try {
      await vi.waitFor(() => expect(wrapper.find(".ProseMirror a[href]").exists()).toBe(true));
      const link = wrapper.get(".ProseMirror a[href]").element;
      const modified = new MouseEvent("click", { bubbles: true, cancelable: true, button: 0, ctrlKey: true });
      link.dispatchEvent(modified);
      expect(modified.defaultPrevented).toBe(false);
      const plain = new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 });
      link.dispatchEvent(plain);
      expect(plain.defaultPrevented).toBe(true);
      expect(location.hash).toBe("#/pages/11111111-2222-4333-8444-555555555555");
    } finally {
      wrapper.unmount();
      history.replaceState(null, "", "/");
    }
  });
  // 4.0: in the text being written a click puts the caret into the link; Ctrl/Cmd+click, the middle button and the link's bar open it.
  it("leaves a plain click on a link to the caret while writing and opens it on Ctrl+click, the middle button and the link's bar", async () => {
    const value = readDocument(JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph", content: [
      { type: "text", text: "Viz", marks: [{ type: "link", attrs: { href: "#/pages/11111111-2222-4333-8444-555555555555" } }] }] }] } }));
    const wrapper = mount(DocumentEditor, { props: { value, readonly: false, dark: false, accent: "blue", spaceId: "test" }, attachTo: document.body });
    const open = vi.spyOn(window, "open").mockReturnValue(null);
    try {
      await vi.waitFor(() => expect(wrapper.find(".ProseMirror[contenteditable=true] a[href]").exists()).toBe(true));
      const link = wrapper.get(".ProseMirror a[href]").element;
      const plain = new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 });
      link.dispatchEvent(plain);
      expect(plain.defaultPrevented).toBe(false);
      expect(open).not.toHaveBeenCalled();
      expect(location.hash).toBe("");
      for (const event of [new MouseEvent("click", { bubbles: true, cancelable: true, button: 0, ctrlKey: true }), new MouseEvent("auxclick", { bubbles: true, cancelable: true, button: 1 })]) {
        link.dispatchEvent(event);
        expect(event.defaultPrevented).toBe(true);
      }
      expect(open).toHaveBeenCalledTimes(2);
      expect(open).toHaveBeenLastCalledWith(expect.stringContaining("#/pages/11111111-2222-4333-8444-555555555555"), "_blank", "noopener,noreferrer");
      // The bar's „Otevřít odkaz“: a wiki page in this window; another address is left to the editor (a new tab).
      const asked = (href: string) => new CustomEvent("markdown-follow-link", { bubbles: true, cancelable: true, detail: { href } });
      const outside = asked("https://example.test/");
      link.dispatchEvent(outside);
      expect(outside.defaultPrevented).toBe(false);
      link.dispatchEvent(asked("#/pages/11111111-2222-4333-8444-555555555555"));
      expect(location.hash).toBe("#/pages/11111111-2222-4333-8444-555555555555");
    } finally {
      open.mockRestore();
      wrapper.unmount();
      history.replaceState(null, "", "/");
    }
  });
  it("emits editIntent for a key into the read-only text, not for a click", async () => {
    const value = readDocument(JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Text" }] }] } }));
    const wrapper = mount(DocumentEditor, { props: { value, readonly: true, dark: false, accent: "blue", spaceId: "test", canEdit: true }, attachTo: document.body });
    try {
      await vi.waitFor(() => expect(wrapper.find(".ProseMirror p").exists()).toBe(true));
      const paragraph = wrapper.get(".ProseMirror p").element;
      paragraph.dispatchEvent(new MouseEvent("click", { bubbles: true, button: 0 }));
      expect(wrapper.emitted("editIntent")).toBeUndefined();
      wrapper.get(".ProseMirror").element.dispatchEvent(new KeyboardEvent("keydown", { key: "a", bubbles: true }));
      expect(wrapper.emitted("editIntent")).toEqual([[]]);
    } finally { wrapper.unmount(); }
  });
});
