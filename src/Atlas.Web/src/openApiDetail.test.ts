import { afterEach, describe, expect, it, vi } from "vitest";
import { effectScope, nextTick, ref, type EffectScope } from "vue";
import { flushPromises } from "@vue/test-utils";
import { normalizeOpenApiShape as normalizeOpenApi, type OpenApiData, type OpenApiSourceEditorOptions } from "@atlas/markdown/plugins/openapi";
import { openApiDetailMessages, useOpenApiDetail, type OpenApiDetailContext, type OpenApiDetailOptions } from "./openApiDetail";

const spec = (title: string, extra = "") => `openapi: 3.1.0\ninfo:\n  title: ${title}\n  version: '1.0'\npaths: {}\n${extra}`;
const changed = spec("Billing", "components: {}\n");

/** One OpenAPI node at position 5 of a minimal editor stand-in; `update` behaves like the module's. */
function block(filename = "billing.yaml", source = spec("Billing")) {
  let data: OpenApiData = normalizeOpenApi({ version: 1, filename, source });
  let present = true;
  const listeners = new Set<() => void>();
  const emit = () => listeners.forEach(listener => listener());
  const editor = {
    isEditable: true,
    state: { doc: { nodeAt: (position: number) => present && position === 5 ? { type: { name: "openapi" }, attrs: { data } } : null } },
    on: (_event: string, listener: () => void) => { listeners.add(listener); },
    off: (_event: string, listener: () => void) => { listeners.delete(listener); },
  };
  const lifetime = new AbortController();
  const update = vi.fn((next: OpenApiData) => { if (!editor.isEditable || lifetime.signal.aborted) return; data = next; emit(); });
  const context = {
    anchor: document.createElement("div"), data, document: { type: "doc" }, signal: lifetime.signal,
    editor, getPos: () => present ? 5 : undefined, update, onEditableChange: () => () => {},
  } as unknown as OpenApiDetailContext;
  return {
    context, editor, update, lifetime,
    get data() { return data; },
    /** Another author (or undo) changes the stored definition. */
    setSource(next: string) { data = { ...data, source: next }; emit(); },
    remove() { present = false; emit(); },
  };
}

interface FakeSource { value: string; options: OpenApiSourceEditorOptions; setValue: ReturnType<typeof vi.fn>; destroy: ReturnType<typeof vi.fn>; layout: ReturnType<typeof vi.fn>; focus: ReturnType<typeof vi.fn>; type(value: string): void }
let scope: EffectScope | undefined;
afterEach(() => { scope?.stop(); scope = undefined; });

function setup(overrides: Partial<OpenApiDetailOptions> = {}) {
  const readonly = ref(false);
  const session = ref("collaboration:1");
  const sources: FakeSource[] = [];
  const references: ReturnType<typeof vi.fn>[] = [];
  const mountSource = vi.fn(async (_host: HTMLElement, options: OpenApiSourceEditorOptions) => {
    const source: FakeSource = {
      value: options.source, options,
      setValue: vi.fn((value: string) => { source.value = value; }), destroy: vi.fn(), layout: vi.fn(), focus: vi.fn(),
      type(value: string) { source.value = value; options.onChange?.(value); },
    };
    sources.push(source);
    return { getValue: () => source.value, setValue: source.setValue, setFilename() {}, focus: source.focus, layout: source.layout, destroy: source.destroy };
  });
  const mountReference = vi.fn((_host: HTMLElement, _data: OpenApiData, _options?: object) => { const dispose = vi.fn(); references.push(dispose); return dispose; });
  const prefetchSource = vi.fn();
  scope = effectScope();
  const detail = scope.run(() => useOpenApiDetail({
    readonly: () => readonly.value, session: () => session.value,
    mountSource: mountSource as NonNullable<OpenApiDetailOptions["mountSource"]>, mountReference, prefetchSource, ...overrides,
  }))!;
  detail.sourceHost.value = document.createElement("div");
  detail.referenceHost.value = document.createElement("div");
  return { detail, readonly, session, sources, references, mountSource, mountReference, prefetchSource };
}
async function settle() { await flushPromises(); await nextTick(); await flushPromises(); }

describe("OpenAPI detail state", () => {
  it("opens the API view lazily with Try It off and never loads the source editor for it", async () => {
    const { detail, mountSource, mountReference } = setup();
    const target = block();
    expect(detail.openDetail(target.context, "api")).toBe(true);
    await settle();
    expect(detail.open.value).toBe(true);
    expect(detail.view.value).toBe("api");
    // An editor gets both views at once; Monaco still loads only with the YAML view.
    expect(detail.tabs.value.map(tab => tab.label)).toEqual(["Dokumentace", "YAML"]);
    expect(detail.title.value).toBe("billing.yaml");
    expect(mountReference).toHaveBeenCalledTimes(1);
    expect(mountReference.mock.calls[0]![2]).toEqual({ allowRequests: false });
    expect(mountSource).not.toHaveBeenCalled();
    detail.activate("yaml");
    await settle();
    expect(mountSource).toHaveBeenCalledTimes(1);
    expect(detail.tabs.value.map(tab => tab.id)).toEqual(["api", "yaml"]);
  });

  it("offers a reader only Dokumentace and a download-only block only YAML", async () => {
    const { detail, readonly, mountSource } = setup();
    readonly.value = true;
    detail.openDetail(block().context, "api");
    await settle();
    expect(detail.tabs.value.map(tab => tab.id)).toEqual(["api"]);
    detail.requestClose(); detail.closed();
    readonly.value = false;
    const download = block();
    download.update({ ...download.data, mode: "download" });
    detail.openDetail(download.context, "yaml");
    await settle();
    expect(mountSource).toHaveBeenCalledTimes(1);
    expect(detail.tabs.value.map(tab => tab.id)).toEqual(["yaml"]);
  });

  it("puts the keyboard into the YAML editor once it has loaded", async () => {
    const { detail, sources } = setup();
    detail.openDetail(block().context, "yaml");
    await settle();
    expect(sources[0]!.focus).toHaveBeenCalledTimes(1);
  });

  it("tracks a dirty draft and applies it to the block as one update", async () => {
    const { detail, sources } = setup();
    const target = block();
    detail.openDetail(target.context, "yaml");
    await settle();
    expect(detail.dirty.value).toBe(false);
    sources[0]!.type(changed);
    expect(detail.dirty.value).toBe(true);
    expect(detail.apply()).toBe(true);
    expect(target.update).toHaveBeenCalledTimes(1);
    expect(target.data.source).toBe(changed);
    expect(detail.dirty.value).toBe(false);
    expect(detail.baseline.value).toBe(changed);
  });

  it("refuses to apply over a concurrent change of the same source and keeps the draft", async () => {
    const { detail, sources } = setup();
    const target = block();
    detail.openDetail(target.context, "yaml");
    await settle();
    sources[0]!.type(changed);
    target.setSource(spec("Billing v2"));
    await settle();
    expect(detail.apply()).toBe(false);
    expect(detail.error.value).toBe(openApiDetailMessages.stale);
    expect(target.update).not.toHaveBeenCalled();
    expect(detail.draft.value).toBe(changed);
    expect(sources[0]!.setValue).not.toHaveBeenCalled();
  });

  it("follows a concurrent change while clean and reports an invalid draft without writing", async () => {
    const { detail, sources } = setup();
    const target = block();
    detail.openDetail(target.context, "yaml");
    await settle();
    target.setSource(spec("Billing v2"));
    await settle();
    expect(sources[0]!.setValue).toHaveBeenCalledWith(spec("Billing v2"), "billing.yaml");
    sources[0]!.type("openapi: 2.0");
    expect(detail.apply()).toBe(false);
    expect(detail.error.value).toContain("OpenAPI");
    expect(target.update).not.toHaveBeenCalled();
  });

  it("discards the draft and restores the stored source", async () => {
    const { detail, sources } = setup();
    const target = block();
    detail.openDetail(target.context, "yaml");
    await settle();
    sources[0]!.type(changed);
    detail.discard();
    expect(detail.dirty.value).toBe(false);
    expect(sources[0]!.setValue).toHaveBeenLastCalledWith(target.data.source, "billing.yaml");
  });

  it("asks before closing a dirty draft: continue keeps it, discard closes", async () => {
    const { detail, sources } = setup();
    const target = block();
    detail.openDetail(target.context, "yaml");
    await settle();
    detail.requestClose();
    expect(detail.open.value).toBe(false);
    detail.openDetail(target.context, "yaml");
    await settle();
    sources.at(-1)!.type(changed);
    detail.requestClose();
    expect(detail.confirmation.value).toMatchObject({ unapplicable: false });
    detail.choose("continue");
    expect(detail.confirmation.value).toBeUndefined();
    expect(detail.open.value).toBe(true);
    expect(detail.draft.value).toBe(changed);
    detail.requestClose();
    detail.choose("discard");
    expect(detail.open.value).toBe(false);
    expect(target.update).not.toHaveBeenCalled();
  });

  it("resolves the host's save guard by apply, discard or continue", async () => {
    const { detail, sources } = setup();
    const target = block();
    await expect(detail.resolveDraft()).resolves.toBe(true);
    detail.openDetail(target.context, "yaml");
    await settle();
    sources[0]!.type(changed);
    const kept = detail.resolveDraft();
    detail.choose("continue");
    await expect(kept).resolves.toBe(false);
    const applied = detail.resolveDraft();
    detail.choose("apply");
    await expect(applied).resolves.toBe(true);
    expect(target.data.source).toBe(changed);
    expect(detail.open.value).toBe(true);
    sources[0]!.type(spec("Billing", "tags: []\n"));
    target.setSource(spec("Elsewhere"));
    await settle();
    const refused = detail.resolveDraft();
    detail.choose("apply");
    await expect(refused).resolves.toBe(false);
    expect(detail.error.value).toBe(openApiDetailMessages.stale);
    expect(detail.view.value).toBe("yaml");
  });

  it("offers an unapplied draft again for the same definition after the workspace was torn down, without any page guard", async () => {
    const first = setup();
    const target = block();
    first.detail.openDetail(target.context, "yaml");
    await settle();
    first.sources[0]!.type(changed);
    first.detail.dispose();
    expect(first.detail.draft.value).toBeUndefined();

    const second = setup();
    second.detail.openDetail(block().context, "yaml");
    await settle();
    expect(second.detail.draft.value).toBe(changed);
    expect(second.detail.dirty.value).toBe(true);
    expect(second.mountSource.mock.calls[0]![1].source).toBe(changed);
  });

  it("keeps a dirty draft downloadable when the block is deleted, and closes a clean one", async () => {
    const { detail, sources } = setup();
    const clean = block();
    detail.openDetail(clean.context, "yaml");
    await settle();
    clean.remove();
    await settle();
    expect(detail.open.value).toBe(false);
    detail.closed();

    const target = block();
    detail.openDetail(target.context, "yaml");
    await settle();
    const source = sources.at(-1)!;
    source.type(changed);
    target.remove();
    await settle();
    expect(detail.open.value).toBe(true);
    expect(detail.retained.value).toBe("lost");
    expect(source.destroy).toHaveBeenCalled();
    expect(detail.apply()).toBe(false);
    expect(detail.draft.value).toBe(changed);
    detail.requestClose();
    expect(detail.confirmation.value).toMatchObject({ unapplicable: true });
    detail.choose("apply");
    expect(detail.open.value).toBe(true);
    detail.requestClose();
    detail.choose("discard");
    expect(detail.open.value).toBe(false);
  });

  it("treats an aborted block context (editor remount) and a new editor session as a lost block", async () => {
    const { detail, sources, session } = setup();
    const target = block();
    detail.openDetail(target.context, "yaml");
    await settle();
    sources[0]!.type(changed);
    target.lifetime.abort();
    await settle();
    expect(detail.retained.value).toBe("lost");
    expect(detail.open.value).toBe(true);
    detail.discardDraft();
    await settle();
    expect(detail.open.value).toBe(false);
    detail.closed();

    const next = block();
    detail.openDetail(next.context, "api");
    await settle();
    session.value = "collaboration:2";
    await settle();
    expect(detail.detached.value).toBe(true);
    expect(detail.open.value).toBe(false);
  });

  it("closes the YAML view on permission loss, keeps a draft downloadable and the API view readable", async () => {
    const { detail, sources, readonly, references } = setup();
    const target = block();
    detail.openDetail(target.context, "api");
    await settle();
    detail.activate("yaml");
    await settle();
    readonly.value = true;
    await settle();
    expect(sources[0]!.destroy).toHaveBeenCalled();
    expect(detail.tabs.value.map(tab => tab.id)).toEqual(["api"]);
    expect(detail.view.value).toBe("api");
    expect(references[0]).not.toHaveBeenCalled();
    expect(detail.openDetail(target.context, "yaml")).toBe(false);
    expect(detail.activate("yaml")).toBe(false);

    readonly.value = false;
    detail.activate("yaml");
    await settle();
    sources.at(-1)!.type(changed);
    readonly.value = true;
    await settle();
    expect(detail.retained.value).toBe("readonly");
    expect(detail.view.value).toBe("yaml");
    expect(detail.apply()).toBe(false);
    expect(target.update).not.toHaveBeenCalled();
  });

  it("refuses YAML for a readonly or aborted context and API for download-only mode", () => {
    const { detail, readonly } = setup();
    const target = block();
    readonly.value = true;
    expect(detail.openDetail(target.context, "yaml")).toBe(false);
    readonly.value = false;
    target.editor.isEditable = false;
    expect(detail.openDetail(target.context, "yaml")).toBe(false);
    const download = block();
    expect(detail.openDetail({ ...download.context, data: { ...download.data, mode: "download" } }, "api")).toBe(false);
    download.lifetime.abort();
    expect(detail.openDetail(download.context, "api")).toBe(false);
    expect(detail.open.value).toBe(false);
  });

  it("destroys a source editor that finishes loading after the detail closed or moved on", async () => {
    let finish!: () => void;
    const late = { getValue: () => "", setValue() {}, setFilename() {}, focus() {}, layout() {}, destroy: vi.fn() };
    const mountSource = vi.fn(() => new Promise<typeof late>(resolve => { finish = () => resolve(late); }));
    const { detail } = setup({ mountSource });
    detail.openDetail(block().context, "yaml");
    await settle();
    expect(mountSource).toHaveBeenCalledTimes(1);
    expect(detail.loading.value).toBe(true);
    detail.requestClose();
    detail.closed();
    finish();
    await settle();
    expect(late.destroy).toHaveBeenCalled();
    expect(detail.loading.value).toBe(false);
  });

  it("confirms before switching to another block while a draft is dirty", async () => {
    const { detail, sources } = setup();
    const first = block("first.yaml"), second = block("second.yaml", spec("Second"));
    detail.openDetail(first.context, "yaml");
    await settle();
    sources[0]!.type(changed);
    expect(detail.openDetail(second.context, "api")).toBe(true);
    expect(detail.confirmation.value).toBeDefined();
    expect(detail.title.value).toBe("first.yaml");
    detail.choose("discard");
    await settle();
    expect(detail.title.value).toBe("second.yaml");
    expect(detail.view.value).toBe("api");
    expect(detail.dirty.value).toBe(false);
    expect(first.update).not.toHaveBeenCalled();
  });

  it("keeps draft and tabs when the same block is reopened from another entry point", async () => {
    const { detail, sources } = setup();
    const target = block();
    detail.openDetail(target.context, "api");
    await settle();
    detail.activate("yaml");
    await settle();
    sources[0]!.type(changed);
    const toolbar = { ...target.context, anchor: document.createElement("div") } as OpenApiDetailContext;
    expect(detail.openDetail(toolbar, "yaml")).toBe(true);
    expect(detail.confirmation.value).toBeUndefined();
    expect(detail.draft.value).toBe(changed);
    expect(detail.tabs.value.map(tab => tab.id)).toEqual(["api", "yaml"]);
  });

  it("prefetches the source editor only while editing is allowed", () => {
    const { detail, readonly, prefetchSource } = setup();
    detail.prefetchSource();
    readonly.value = true;
    detail.prefetchSource();
    expect(prefetchSource).toHaveBeenCalledTimes(1);
  });
});
