import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import type { MarkdownAction, MarkdownEditorController } from "@atlas/markdown/core";
import type { Editor } from "@tiptap/core";
import { emptyDocument } from "../document";
import DocumentEditor, { codeBlockHighlightingReady } from "./DocumentEditor.vue";

// Editor 0.9.0: the slash menu continues after a space with a parameter (`/stránka vpn` starts `[[vpn`, `/úkol 123`
// opens the YouTrack picker with „123“). Atlas registers the built-in pages and YouTrack plugins, so the parameter has
// to reach Atlas's own directories: the wiki search and the space's YouTrack search.
const call = vi.hoisted(() => vi.fn(async (_path: string, _init?: unknown): Promise<unknown> => []));
vi.mock("../api", async original => ({ ...await original<object>(), api: call }));
vi.mock("../pageSource", async original => ({ ...await original<object>(), prefetchPageSource: vi.fn() }));

// App waits for highlighting before mounting; otherwise its late readiness re-keys
// this direct editor mount and aborts an open picker before its debounced search.
beforeAll(() => codeBlockHighlightingReady);
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); call.mockReset(); vi.restoreAllMocks(); });

async function liveEditor() {
  call.mockImplementation(async path => path.startsWith("/search") ? { items: [] } : []);
  wrapper = mount(DocumentEditor, {
    props: { value: emptyDocument(), readonly: false, dark: false, accent: "blue" as const, spaceId: "space-1", pageId: "page-1", documentTitle: "Runbook" },
    attachTo: document.body,
  });
  await vi.waitFor(() => expect(wrapper!.findComponent({ name: "MarkdownEditor" }).exists()).toBe(true));
  await flushPromises();
  const controller = (wrapper.getComponent({ name: "MarkdownEditor" }).vm as unknown as { controller: MarkdownEditorController }).controller;
  await vi.waitFor(() => expect(controller.state.editable).toBe(true));
  return controller;
}
const slashAction = (controller: MarkdownEditorController, id: string) => {
  const action = controller.actions.slash.find(candidate => candidate.id === id);
  expect(action, id).toBeDefined();
  return action as MarkdownAction & { parameter: NonNullable<MarkdownAction["parameter"]> };
};
const requested = (prefix: string) => call.mock.calls.map(([path]) => path).filter(path => path.startsWith(prefix));

describe("slash parameter in the page editor", () => {
  it("offers the page link and YouTrack actions with a parameter under their Czech keywords", async () => {
    const controller = await liveEditor();
    const pages = slashAction(controller, "pages.search");
    const issues = slashAction(controller, "youtrack.search");
    expect(pages.parameter).toBeDefined();
    expect(issues.parameter).toBeDefined();
    // The editor folds diacritics, so `/stránka` and `/úkol` match these keywords.
    expect(pages.keywords).toContain("stranka");
    expect(issues.keywords).toContain("ukol");
  });

  it("starts the page link search with the typed text through Atlas's wiki search", async () => {
    const controller = await liveEditor();
    const ed = document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")!.editor!;
    controller.focus();
    // Tiptap focuses on the next animation frame; slash commands are selected
    // only after the editor has focus, or the suggestion session exits immediately.
    await vi.waitFor(() => expect(ed.isFocused).toBe(true));
    expect(controller.dispatch(slashAction(controller, "pages.search").parameter.command("vpn")!)).toBe(true);
    await vi.waitFor(() => expect(requested("/search?q=vpn")).toEqual([
      "/search?q=vpn&limit=10",
      "/search?q=vpn&spaceId=space-1&limit=10",
    ]));
  });

  it("opens the YouTrack picker with the typed text and searches the space's YouTrack", async () => {
    const controller = await liveEditor();
    const ed = document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")!.editor!;
    controller.focus();
    // Tiptap focuses on the next animation frame; slash commands are selected
    // only after the editor has focus, or the suggestion session exits immediately.
    await vi.waitFor(() => expect(ed.isFocused).toBe(true));
    expect(controller.dispatch(slashAction(controller, "youtrack.search").parameter.command("123")!)).toBe(true);
    await vi.waitFor(() => expect(requested("/integrations/youtrack/search")).toContain("/integrations/youtrack/search?q=123&spaceId=space-1"));
  });

  it.each([["/stránka vpn", "vpn"], ["/úkol 123", "123"]])("turns a typed `%s` into the parameter row of the slash menu", async (typed, text) => {
    await liveEditor();
    const editor = document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")?.editor;
    expect(editor).toBeDefined();
    editor!.commands.focus();
    editor!.commands.insertContent(typed);
    await vi.waitFor(() => expect(document.body.textContent).toContain(`vyhledat „${text}“`));
  });
});
