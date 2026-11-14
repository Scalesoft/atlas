import { afterEach, describe, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { createMarkdownEditorController, type MarkdownPlugin } from "@atlas/markdown/core";
import hostSchema from "../../../tests/fixtures/host-document-schema.json";
import { atlasContentPlugins, createDocumentWidgets, gateWrites, openApiPolicy, type DocumentWidgetContext } from "./documentWidgets";
import { useModules } from "./composables/useModules";
import type { ProjectorHost } from "./projector/requirementsPlugin";
import { emptyDocument, readDocument } from "./document";
import DocumentEditor from "./components/DocumentEditor.vue";
import HistoryRevisionPreview from "./components/HistoryRevisionPreview.vue";

const contexts: DocumentWidgetContext[] = [{ kind: "edit", uploaderName: "Jana Nováková" }, { kind: "history" }, { kind: "public" }];
const byId = (plugins: readonly { id: string; version: number }[]) => [...plugins].sort((a, b) => a.id.localeCompare(b.id));

/** The collaboration schema plugins a controller built from `plugins` binds and saves. */
function schemaPlugins(plugins: MarkdownPlugin[]) {
  const controller = createMarkdownEditorController({ profile: "rich", source: { kind: "local", value: emptyDocument() }, plugins });
  try {
    controller.attachView(document.createElement("div"), { ariaLabel: "Schema" });
    return byId(controller.getValue().plugins ?? []);
  } finally { controller.destroy(); }
}

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; });

describe("OpenAPI policy per display context", () => {
  it("keeps Try It off in every context and never supplies a proxy or request transport", () => {
    for (const context of contexts) {
      const policy = openApiPolicy(context);
      expect(policy.allowRequests, context.kind).toBe(false);
      expect(policy, context.kind).not.toHaveProperty("proxyUrl");
      expect(policy, context.kind).not.toHaveProperty("customFetch");
    }
  });
  it("captures the editing user's display name only while editing", () => {
    expect(openApiPolicy({ kind: "edit", uploaderName: "  Jana Nováková " })).toEqual({ allowRequests: false, uploaderName: "Jana Nováková" });
    expect(openApiPolicy({ kind: "edit", uploaderName: "   " })).toEqual({ allowRequests: false });
    expect(openApiPolicy({ kind: "edit" })).toEqual({ allowRequests: false });
    expect(openApiPolicy({ kind: "history" })).toEqual({ allowRequests: false });
    expect(openApiPolicy({ kind: "public" })).toEqual({ allowRequests: false });
  });
  it("passes a host detail workspace through only for editing", () => {
    const openDetail = () => true;
    expect(openApiPolicy({ kind: "edit", openDetail }).openDetail).toBe(openDetail);
  });
});

describe("3.6-0: read services and write capabilities of the page", () => {
  it("asks canWrite() at action time: write capabilities answer nothing while closed, read services stay on", async () => {
    let writable = false;
    const signal = new AbortController().signal;
    const page = { id: "p", title: "Příručka" } as never;
    const issue = { key: "IN-1" } as never;
    const people = Object.assign(vi.fn(async () => [{ name: "Jana" }] as never[]), { cached: () => [{ name: "Jana" }] as never[] });
    const context = gateWrites<DocumentWidgetContext>({ kind: "edit", canWrite: () => writable, people, openDetail: () => true, pickSecretReplacement: async () => "secret",
      upload: { upload: async () => ({ name: "a.txt", url: "/a" }) }, pages: { search: async () => [page], resolve: async () => page },
      youTrack: { search: async () => [issue], resolve: async () => issue },
      headingAnchors: { collaborationReady: () => true, getHeadingUrl: id => `#${id}` },
      projector: { spaceId: () => "s", pageId: () => "p", open: () => {}, api: { setCatalogPage: async () => ({ isCatalog: true }), createDeployment: async () => ({ id: "d" }) } as unknown as ProjectorHost["api"] } });
    if (context.kind !== "edit") throw new Error("edit");
    const closed = async () => [await context.people!("j", signal), context.people!.cached!("j"),
      await context.pickSecretReplacement!({ secretId: "x", layout: "inline", signal }), await context.pages!.search("p", { signal }),
      await context.youTrack!.search("IN", { signal }), context.headingAnchors!.collaborationReady!()];
    expect(await closed()).toEqual([[], [], null, [], [], false]);
    await expect(context.upload!.upload(new File([], "a.txt"), { signal })).rejects.toThrow("Stránku teď nelze upravovat.");
    await expect(context.projector!.api.setCatalogPage("p", true)).rejects.toThrow();
    // Read services do not depend on the right; the OpenAPI detail's API view is reading.
    expect(context.openDetail!({} as never, "api")).toBe(true);
    expect([await context.pages!.resolve!("p", { signal }), await context.youTrack!.resolve!("IN-1", { signal }), context.headingAnchors!.getHeadingUrl!("h")]).toEqual([page, issue, "#h"]);
    writable = true;
    expect(await closed()).toEqual([[{ name: "Jana" }], [{ name: "Jana" }], "secret", [page], [issue], true]);
    await expect(context.upload!.upload(new File([], "a.txt"), { signal })).resolves.toMatchObject({ name: "a.txt" });
    // History and the public view have no capabilities to gate.
    expect(gateWrites({ kind: "history" })).toEqual({ kind: "history" });
  });
});

describe("document widget registration", () => {
  it("U6: offers the requirement inserts in the page menu (the editor's toolbar actions) with icons while the projector module is on", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ modules: [{ id: "projector" }] }), { headers: { "content-type": "application/json" } })));
    await useModules().start();
    const host = { spaceId: () => "space", pageId: () => "page", api: {} as ProjectorHost["api"], open: () => {} };
    const controller = createMarkdownEditorController({ profile: "rich", source: { kind: "local", value: emptyDocument() }, plugins: createDocumentWidgets({ kind: "edit", projector: host }) });
    try {
      const offered = controller.actions.toolbar.filter(action => ["Požadavek", "Moduly produktu", "Souhrn nasazení"].includes(action.label));
      expect(offered.map(action => action.label).sort()).toEqual(["Moduly produktu", "Požadavek", "Souhrn nasazení"]);
      expect(offered.every(action => action.icon?.startsWith("svg:"))).toBe(true);
    } finally { controller.destroy(); useModules().stop(); vi.unstubAllGlobals(); }
  });
  it("registers the same plugin ids in every context, including OpenAPI", () => {
    const ids = contexts.map(context => createDocumentWidgets(context).map(plugin => plugin.id));
    expect(ids[0]).toContain("openapi");
    // Projector's requirement blocks: an optional module's nodes, yet every page with them must open in every context.
    expect(ids[0]).toContain("atlas-requirements");
    expect(ids[1]).toEqual(ids[0]);
    expect(ids[2]).toEqual(ids[0]);
    expect(new Set(contexts.map(context => JSON.stringify(schemaPlugins(createDocumentWidgets(context)))))).toHaveProperty("size", 1);
  });
  it("gives every surface the same content plugins, the public view only without YouTrack", () => {
    const ids = contexts.map(context => atlasContentPlugins(context).map(plugin => plugin.id));
    expect(ids[0]).toEqual(expect.arrayContaining(["editor-tools", "attachments", "heading-anchors", "youtrack"]));
    expect(ids[1]).toEqual(ids[0]);
    expect(ids[2]).toEqual(ids[0]!.filter(id => id !== "youtrack"));
    expect(schemaPlugins(atlasContentPlugins(contexts[0]!))).toEqual(byId(hostSchema.plugins));
  });
  it("binds exactly the newest collaboration schema the server knows (tests/fixtures/host-document-schema.json)", async () => {
    wrapper = mount(DocumentEditor, {
      props: { value: emptyDocument(), readonly: false, dark: false, accent: "blue", spaceId: "space" },
      attachTo: document.body,
    });
    await vi.waitFor(() => expect(wrapper!.emitted("ready")).toBeTruthy());
    const saved = readDocument((wrapper.vm as unknown as { capture(): { contentJson: string } }).capture().contentJson);
    expect(byId(saved.plugins ?? [])).toEqual(byId(hostSchema.plugins));
    // The built-in revision the editor declares (0.7: 3), not a copy of it: an editor upgrade that raises it fails here
    // until the server's KnownHostSchemas and this fixture follow.
    const controller = createMarkdownEditorController({ profile: "rich", source: { kind: "local", value: emptyDocument() }, plugins: createDocumentWidgets(contexts[0]!) });
    try { expect(hostSchema.version).toBe(controller.schema.version); } finally { controller.destroy(); }
  });
  it("renders history with the same schema as the editor", () => {
    wrapper = mount(HistoryRevisionPreview, { props: { contentJson: JSON.stringify(emptyDocument()), dark: false, accent: "blue" } });
    const plugins = wrapper.findComponent({ name: "MarkdownRenderer" }).props("plugins") as MarkdownPlugin[];
    expect(schemaPlugins(plugins)).toEqual(byId(hostSchema.plugins));
  });
});
