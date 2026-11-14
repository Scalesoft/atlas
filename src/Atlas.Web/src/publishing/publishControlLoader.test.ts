// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import { defineComponent } from "vue";
import { createPublishControlLoader } from "./publishControlLoader";

const Control = defineComponent({ render: () => null });
const flush = () => new Promise(resolve => setTimeout(resolve, 0));

describe("createPublishControlLoader", () => {
  it("hands out the component once its chunk is in, and fetches it once", async () => {
    const load = vi.fn(async () => ({ default: Control }));
    const loader = createPublishControlLoader(load);
    expect(loader.ready.value).toBeNull();
    await loader.prepare();
    await loader.prepare();
    expect(loader.ready.value).toBe(Control);
    expect(load).toHaveBeenCalledTimes(1);
  });

  it("treats a failed fetch as nothing: no rejection, the async wrapper still loads the control when it is rendered", async () => {
    const load = vi.fn(async () => { throw new Error("Failed to fetch dynamically imported module"); });
    const loader = createPublishControlLoader(load);
    // Started in the background as the app does (nobody awaits it): a rejection would be unhandled and fail the run.
    void loader.prepare();
    await flush();
    expect(loader.ready.value).toBeNull();
    // A later intent tries again.
    await expect(loader.prepare()).resolves.toBeUndefined();
    expect(load).toHaveBeenCalledTimes(2);
  });

  it("does nothing after the app is gone: no fetch, and a fetch that finishes late is ignored", async () => {
    let finish!: (module: { default: typeof Control }) => void;
    const load = vi.fn(() => new Promise<{ default: typeof Control }>(resolve => { finish = resolve; }));
    const loader = createPublishControlLoader(load);
    void loader.prepare();
    loader.dispose();
    finish({ default: Control });
    await flush();
    expect(loader.ready.value).toBeNull();
    await loader.prepare();
    expect(load).toHaveBeenCalledTimes(1);
  });
});
