import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import type { MarkdownEditorController } from "@atlas/markdown/core";
import {
  insertOpenApi,
  mountOpenApiReference,
  mountOpenApiSourceEditor,
  type OpenApiSourceEditorOptions,
} from "@atlas/markdown/plugins/openapi";
import { emptyDocument, readDocument } from "../document";
import { atlasContentPlugins } from "../documentWidgets";
import type { OpenApiDetailContext } from "../openApiDetail";
import DocumentEditor from "./DocumentEditor.vue";

// Scalar and Monaco never load in these tests: the detail's lazy entry points are replaced, the rest of
// the plugin (node, card runtime, normalization) is the real one.
const sourceChanges: ((source: string) => void)[] = [];
vi.mock("@atlas/markdown/plugins/openapi", async original => ({
  ...await original<typeof import("@atlas/markdown/plugins/openapi")>(),
  mountOpenApiReference: vi.fn(() => () => {}),
  mountOpenApiSourceEditor: vi.fn(async (_host: HTMLElement, options: OpenApiSourceEditorOptions) => {
    let value = options.source;
    sourceChanges.push(source => { value = source; options.onChange?.(source); });
    return { getValue: () => value, setValue: (next: string) => { value = next; }, setFilename() {}, focus() {}, layout() {}, destroy() {} };
  }),
  prefetchOpenApiSourceEditor: vi.fn(),
}));
vi.mock("../documentWidgets", async original => {
  const actual = await original<typeof import("../documentWidgets")>();
  return { ...actual, atlasContentPlugins: vi.fn(actual.atlasContentPlugins) };
});

const source = "openapi: 3.1.0\ninfo:\n  title: Billing API\n  version: '1.0'\npaths: {}\n";
const edited = source.replace("paths: {}", "paths:\n  /invoices:\n    get:\n      responses: {}");
type Block = { type?: string; attrs?: { data?: { source?: string } } };
const blocks = (contentJson: string) => (readDocument(contentJson).document.content ?? []) as unknown as Block[];
type Exposed = { capture(): { contentJson: string }; resolveDetailDraft(): Promise<boolean> };
const controllerOf = (wrapper: VueWrapper) => (wrapper.findComponent({ name: "MarkdownEditor" }).vm as unknown as { controller: MarkdownEditorController<"rich"> }).controller;

let wrapper: VueWrapper | undefined;
afterEach(() => {
  wrapper?.unmount(); wrapper = undefined;
  sourceChanges.length = 0;
  vi.mocked(atlasContentPlugins).mockClear();
  vi.mocked(mountOpenApiReference).mockClear();
  vi.mocked(mountOpenApiSourceEditor).mockClear();
  document.body.replaceChildren();
});

async function editorWithApi(readonly = false) {
  wrapper = mount(DocumentEditor, {
    props: { value: emptyDocument(), readonly: false, dark: false, accent: "blue", spaceId: "space", pageId: "page" },
    attachTo: document.body,
  });
  await vi.waitFor(() => expect(wrapper!.emitted("ready")).toBeTruthy());
  expect(controllerOf(wrapper).dispatch(insertOpenApi({ version: 1, filename: "billing.yaml", source, mode: "reference" }))).toBe(true);
  if (readonly) await wrapper.setProps({ readonly: true });
  await vi.waitFor(() => expect(wrapper!.find(".md-openapi-card .md-btn-primary").exists()).toBe(true), { timeout: 3000 });
  return wrapper;
}

const yamlTab = (view: VueWrapper) => view.findAll('[role="tab"]').find(tab => tab.text() === "YAML")!;

describe("OpenAPI detail dialog in the page editor", () => {
  it("opens Dokumentace and YAML in a dialog over the page without remounting the editor or losing its undo history", async () => {
    const view = await editorWithApi();
    const controller = controllerOf(view);
    const surface = view.get(".ProseMirror").element;
    const parent = surface.closest<HTMLElement>(".document-pane");
    expect(parent).not.toBeNull();
    expect(controller.query({ type: "undo" }).enabled).toBe(true);
    expect(view.find('[role="tab"]').exists()).toBe(false);

    await view.get(".md-openapi-card .md-btn-primary").trigger("click");
    await flushPromises();
    // A dialog over the page, not a view in its place: the document stays shown where it is and only the dialog scrolls.
    expect(view.find(".openapi-detail-dialog[open]").exists()).toBe(true);
    expect(parent!.style.display).toBe("");
    expect(view.find(".source-pane").exists()).toBe(false);
    expect(view.findAll('[role="tab"]').map(tab => tab.text())).toEqual(["Dokumentace", "YAML"]);
    expect(mountOpenApiReference).toHaveBeenCalledTimes(1);
    expect(vi.mocked(mountOpenApiReference).mock.calls[0]![2]).toEqual({ allowRequests: false });
    expect(mountOpenApiSourceEditor).not.toHaveBeenCalled();

    await yamlTab(view).trigger("mousedown", { button: 0 });
    await flushPromises();
    expect(mountOpenApiSourceEditor).toHaveBeenCalledTimes(1);
    expect(view.get(".openapi-detail-dialog [data-openapi-detail='api']").attributes("hidden")).toBeDefined();

    expect(controllerOf(view)).toBe(controller);
    expect(view.get(".ProseMirror").element).toBe(surface);
    expect(surface.closest(".document-pane")).toBe(parent);
    expect(controller.query({ type: "undo" }).enabled).toBe(true);
    expect(controller.dispatch({ type: "undo" })).toBe(true);
    expect(blocks((view.vm as unknown as Exposed).capture().contentJson).some(node => node.type === "openapi")).toBe(false);
  });

  it("applies a YAML draft only through the Apply / Discard / Continue confirmation and reports the draft", async () => {
    const view = await editorWithApi();
    const controller = controllerOf(view);
    await view.get(".md-openapi-card .md-btn-primary").trigger("click");
    await flushPromises();
    await yamlTab(view).trigger("mousedown", { button: 0 });
    await flushPromises();
    expect(view.text()).toContain("Použitá změna se uloží se stránkou; publikuje se celá stránka.");
    expect(view.findAll("button").some(button => button.text() === "Použít")).toBe(true);
    expect(view.text()).not.toMatch(/Schválit|Schváleno/);
    sourceChanges[0]!(edited);
    await flushPromises();
    expect(view.emitted("detailDraft")?.at(-1)).toEqual([true]);

    const exposed = view.vm as unknown as Exposed;
    const guard = exposed.resolveDetailDraft();
    await flushPromises();
    const dialog = view.findAll("dialog[open]").find(item => item.text().includes("YAML obsahuje změny"))!;
    expect(dialog.text()).toContain("YAML obsahuje změny, které nejsou použité do stránky.");
    await dialog.findAll("button").find(button => button.text() === "Použít")!.trigger("click");
    await expect(guard).resolves.toBe(true);
    await flushPromises();
    expect(view.emitted("detailDraft")?.at(-1)).toEqual([false]);
    expect(blocks(exposed.capture().contentJson).find(item => item.type === "openapi")?.attrs?.data?.source).toBe(edited);
    expect(controllerOf(view)).toBe(controller);
  });

  it("shows a reader only Dokumentace and closes back to the page", async () => {
    const view = await editorWithApi(true);
    await view.get(".md-openapi-card .md-btn-primary").trigger("click");
    await flushPromises();
    const dialog = view.get(".openapi-detail-dialog[open]");
    expect(dialog.findAll('[role="tab"]')).toHaveLength(0);
    expect(dialog.find("[data-openapi-detail='yaml']").exists()).toBe(false);
    expect(mountOpenApiReference).toHaveBeenCalledTimes(1);
    await dialog.get('button[aria-label="Zavřít detail API"]').trigger("click");
    await vi.waitFor(() => expect(view.find(".openapi-detail-dialog[open]").exists()).toBe(false));
  });

  it("refuses YAML while reading but still hands the API view to the dialog", async () => {
    await editorWithApi(true);
    const context = vi.mocked(atlasContentPlugins).mock.calls.at(-1)![0];
    expect(context.kind).toBe("edit");
    const openDetail = context.kind === "edit" ? context.openDetail! : undefined;
    const data = { version: 1 as const, filename: "billing.yaml", source };
    const element = document.createElement("div");
    const fake = { anchor: element, data, document: { type: "doc" }, signal: new AbortController().signal, update: vi.fn(),
      onEditableChange: () => () => {} } as unknown as OpenApiDetailContext & Parameters<NonNullable<typeof openDetail>>[0];
    expect(openDetail!(fake, "yaml")).toBe(false);
    expect(openDetail!(fake, "api")).toBe(true);
    expect(fake.update).not.toHaveBeenCalled();
  });
});
