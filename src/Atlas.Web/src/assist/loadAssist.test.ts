// @vitest-environment node
import { afterEach, describe, expect, it, vi } from "vitest";

// Plan §0 and §12 H.4: the assist module is separable. With `features.editorAssist` off it is never imported (so the
// browser never requests its chunk); otherwise it loads once.
const loaded = { assist: 0, adapter: 0 };

afterEach(() => {
  vi.doUnmock("@atlas/markdown/assist");
  vi.doUnmock("./atlasAssist");
  vi.doUnmock("../aiPreferences");
  vi.resetModules();
  loaded.assist = 0;
  loaded.adapter = 0;
});

function mockModules(assist: Record<string, unknown>, fail = false) {
  vi.doMock("@atlas/markdown/assist", () => {
    loaded.assist++;
    if (fail) throw new Error("chunk failed");
    return assist;
  });
  vi.doMock("./atlasAssist", () => {
    loaded.adapter++;
    return { createAtlasAssist: vi.fn((module: unknown, context: unknown) => ({ id: "assist", apiVersion: 1, module, context })) };
  });
}
const realModule = { createAssistPlugin: vi.fn(), openMarkdownProposalPanel: vi.fn(), defaultAssistStrings: {} };
const on = { features: { editorAssist: true } };

describe("loadEditorAssist", () => {
  it("does not import the assist module when editor assist is off", async () => {
    mockModules(realModule);
    const { loadEditorAssist } = await import("./loadAssist");
    for (const user of [undefined, null, {}, { features: { editorAssist: false } }])
      expect(await loadEditorAssist(user as never)).toBeNull();
    expect(loaded).toEqual({ assist: 0, adapter: 0 });
  });

  it("loads the module and the adapter once and binds the adapter to the module", async () => {
    mockModules(realModule);
    const { loadEditorAssist } = await import("./loadAssist");
    const [first, second] = await Promise.all([loadEditorAssist(on), loadEditorAssist(on)]);
    expect(first).toBe(second);
    expect(loaded).toEqual({ assist: 1, adapter: 1 });
    const plugin = first!({ pageId: () => "page" } as never) as unknown as { module: unknown; context: unknown };
    expect(plugin.module).toBe((await import("@atlas/markdown/assist")) as unknown);
  });

  it("binds the suggestion types the author switched off in Profil → AI", async () => {
    mockModules(realModule);
    const { loadEditorAssist } = await import("./loadAssist");
    const author = { features: { editorAssist: true, disabledSuggestionTypes: ["link-mention", "issue"] } };
    const factory = await loadEditorAssist(author);
    expect(await loadEditorAssist({ features: { editorAssist: true, disabledSuggestionTypes: ["issue", "link-mention"] } })).toBe(factory);
    const { context } = factory!({ pageId: () => "page" } as never) as unknown as { context: { suggestionTypeEnabled(type: string): boolean } };
    expect(context.suggestionTypeEnabled("link-mention")).toBe(false);
    expect(context.suggestionTypeEnabled("contact")).toBe(true);
    // Nothing switched off (the server omits an empty list): every type is offered.
    const { context: open } = (await loadEditorAssist(on))!({ pageId: () => "page" } as never) as unknown as { context: { suggestionTypeEnabled(type: string): boolean } };
    expect(open.suggestionTypeEnabled("link-mention")).toBe(true);
  });

  it("resolves null after a failed chunk and tries again next time", async () => {
    mockModules(realModule, true);
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const { loadEditorAssist } = await import("./loadAssist");
    expect(await loadEditorAssist(on)).toBeNull();
    expect(warn).toHaveBeenCalledTimes(1);
    expect(await loadEditorAssist(on)).toBeNull();
    expect(loaded.assist).toBe(2);
    warn.mockRestore();
  });
});

describe("the module of the current editor mode", () => {
  // The pinned package (0.9.0) and source mode both resolve the specifier to the editor's real module.
  it("is the editor's real module", async () => {
    const module = await import("@atlas/markdown/assist");
    expect(module.createAssistPlugin).toBeTypeOf("function");
    const { loadEditorAssist } = await import("./loadAssist");
    expect(await loadEditorAssist(on)).toBeTypeOf("function");
  });
});
it('loads explicit writing while automatic suggestions are off, without enabling automatic sources', async () => {
  mockModules(realModule);
  const { loadEditorAssist } = await import('./loadAssist');
  const user = { features: { editorAssist: false } };
  const factory = await loadEditorAssist(user, { writingAllowed: true });
  expect(factory).toBeTypeOf('function');
  const plugin = factory!({ pageId: () => 'page' } as never) as unknown as { context: { automaticSuggestionsEnabled(): boolean } };
  expect(plugin.context.automaticSuggestionsEnabled()).toBe(false);
  expect(await loadEditorAssist(user)).toBeNull();
});

it('caches hover/edit preference per author snapshot and refreshes the on-only model gate for a refreshed session', async () => {
  mockModules(realModule);
  const load = vi.fn().mockResolvedValueOnce({editorSuggestions:'on'}).mockResolvedValueOnce({editorSuggestions:'rules'});
  vi.doMock('../aiPreferences',()=>({aiPreferencesApi:{load}}));
  const { loadEditorAssist } = await import('./loadAssist');
  const user = {id:'same-author',features:{editorAssist:true}};
  const factory = await loadEditorAssist(user);
  expect(factory!.modelSuggestionsEnabled!()).toBe(true);
  expect(await loadEditorAssist(user)).toBe(factory); expect(load).toHaveBeenCalledOnce();
  const refreshed = await loadEditorAssist({...user});
  expect(refreshed!.automaticSuggestionsEnabled!()).toBe(true);
  expect(refreshed!.modelSuggestionsEnabled!()).toBe(false); expect(load).toHaveBeenCalledTimes(2);
});
