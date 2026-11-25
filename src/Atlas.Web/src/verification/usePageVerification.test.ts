import { describe, expect, it, vi } from "vitest";
import { effectScope, nextTick, shallowRef } from "vue";
import { flushPromises } from "@vue/test-utils";
import type { Page } from "../api";
import type { PageVerification } from "./pageVerification";

const calls: string[] = [];
vi.mock("../api", async (original) => ({
  ...(await original<typeof import("../api")>()),
  api: vi.fn(async (path: string) => {
    calls.push(path);
    return { ...state("verified"), changedSinceVerification: true, readers: 5 };
  }),
}));

const state = (value: PageVerification["state"]): PageVerification => ({ state: value, stewardId: null, stewardName: null, verifiedAt: null, verifiedUntil: null, verifiedVersion: null,
  changedSinceVerification: false, intervalMonths: null, spaceIntervalMonths: value === "off" ? null : 6 });
const page = (more: Partial<Page>) => ({ id: "p1", publishedVersion: 1, bundle: { verification: state("off") }, ...more }) as unknown as Page;

describe("usePageVerification", () => {
  it("takes the Čtenost from the answer that switches verification on, and reads the state again after a new version is published", async () => {
    const { usePageVerification } = await import("./usePageVerification");
    const shown = shallowRef(page({}));
    const scope = effectScope();
    const view = scope.run(() => usePageVerification(() => shown.value))!;
    expect(view.readers.value).toBeUndefined();
    view.verification.value = { ...state("unverified"), readers: 4 };
    await nextTick(); await flushPromises();
    expect(view.readers.value).toBe(4);
    view.verification.value = state("verified");
    await nextTick(); await flushPromises();
    expect(view.readers.value).toBe(4);
    expect(calls).toEqual([]);
    shown.value = page({ publishedVersion: 2, bundle: shown.value.bundle });
    await nextTick(); await flushPromises(); await vi.dynamicImportSettled(); await flushPromises();
    expect(calls).toEqual(["/pages/p1/verification"]);
    expect(view.verification.value?.changedSinceVerification).toBe(true);
    expect(view.readers.value).toBe(5);
    scope.stop();
  });
});
