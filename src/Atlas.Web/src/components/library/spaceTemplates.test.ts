// @vitest-environment node
import { effectScope, nextTick, ref, type EffectScope } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { pageTemplateApi, type PageTemplateSummary } from "../../pageTemplateApi";
import { useSpaceTemplates } from "./spaceTemplates";

vi.mock("../../pageTemplateApi", () => ({ pageTemplateApi: { list: vi.fn() } }));

const template = (id: string, spaceId: string): PageTemplateSummary => ({ id, spaceId, parentId: null, title: `Šablona ${id}`, version: 1, isPublished: true });
const deferred = <T>() => {
  let resolve!: (value: T) => void, reject!: (reason: unknown) => void;
  const promise = new Promise<T>((done, fail) => { resolve = done; reject = fail; });
  return { promise, resolve, reject };
};
let scope: EffectScope;
beforeEach(() => { vi.clearAllMocks(); scope = effectScope(); });
afterEach(() => scope.stop());

// The loader of O prostoru: the shell hands it the space only while that tab is open, so it asks for nothing otherwise
// and must never show the answer for a space the reader has already left.
describe("useSpaceTemplates", () => {
  it("asks for nothing without a space and lists the templates of the space asked for", async () => {
    const spaceId = ref<string>();
    const templates = scope.run(() => useSpaceTemplates(() => spaceId.value))!;
    expect(pageTemplateApi.list).not.toHaveBeenCalled();
    expect(templates.items.value).toBeUndefined();
    expect(templates.loading.value).toBe(false);

    const answer = deferred<PageTemplateSummary[]>();
    vi.mocked(pageTemplateApi.list).mockReturnValue(answer.promise);
    spaceId.value = "a";
    await nextTick();
    expect(pageTemplateApi.list).toHaveBeenCalledWith("a");
    expect(templates.loading.value).toBe(true);
    answer.resolve([template("t-1", "a")]);
    await vi.waitFor(() => expect(templates.loading.value).toBe(false));
    expect(templates.items.value).toEqual([template("t-1", "a")]);

    // Closing the tab forgets the answer, so opening it again reads the current list.
    spaceId.value = undefined;
    await nextTick();
    expect(templates.items.value).toBeUndefined();
    expect(pageTemplateApi.list).toHaveBeenCalledTimes(1);
  });

  it("drops a late answer for the previous space", async () => {
    const first = deferred<PageTemplateSummary[]>(), second = deferred<PageTemplateSummary[]>();
    vi.mocked(pageTemplateApi.list).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const spaceId = ref<string | undefined>("a");
    const templates = scope.run(() => useSpaceTemplates(() => spaceId.value))!;
    spaceId.value = "b";
    await nextTick();
    expect(templates.items.value).toBeUndefined();

    second.resolve([template("t-b", "b")]);
    await vi.waitFor(() => expect(templates.items.value).toEqual([template("t-b", "b")]));
    first.resolve([template("t-a", "a")]);
    await first.promise;
    await nextTick();
    expect(templates.items.value).toEqual([template("t-b", "b")]);
    expect(templates.loading.value).toBe(false);
  });

  it("keeps a refusal in error and reload asks again", async () => {
    vi.mocked(pageTemplateApi.list).mockRejectedValueOnce(new Error("Offline")).mockResolvedValueOnce([template("t-1", "a")]);
    const templates = scope.run(() => useSpaceTemplates(() => "a"))!;
    await vi.waitFor(() => expect(templates.error.value).toBe("Offline"));
    expect(templates.items.value).toBeUndefined();
    expect(templates.loading.value).toBe(false);

    await templates.reload();
    expect(templates.error.value).toBe("");
    expect(templates.items.value).toEqual([template("t-1", "a")]);
    expect(pageTemplateApi.list).toHaveBeenCalledTimes(2);
  });
});
