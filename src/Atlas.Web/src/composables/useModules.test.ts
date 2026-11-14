// @vitest-environment node
import { afterEach, describe, expect, it, vi } from "vitest";
import { useModules } from "./useModules";

const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } });

afterEach(() => { useModules().stop(); vi.unstubAllGlobals(); });

describe("useModules", () => {
  it("loads the enabled modules once per session, however many ask", async () => {
    const fetchMock = vi.fn(async (_input: string) => json({ modules: [{ id: "pages", version: 1 }, { id: "boards", version: 1 }], contentTypes: ["atlas.page", "atlas.board"] }));
    vi.stubGlobal("fetch", fetchMock);
    const modules = useModules();
    expect(modules.enabled("boards")).toBe(false);
    await Promise.all([modules.start(), modules.start()]);
    await modules.start();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0]![0]).toBe("/api/modules");
    expect(modules.enabled("boards")).toBe(true);
    expect(modules.enabled("projector")).toBe(false);
  });

  it("treats a failure or a malformed answer as no optional modules", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => json({ detail: "x" }, 500)));
    await useModules().start();
    expect(useModules().enabled("boards")).toBe(false);
    useModules().stop();
    vi.stubGlobal("fetch", vi.fn(async () => json({ modules: [{ id: 5 }, "boards", null] })));
    await useModules().start();
    expect(useModules().enabled("boards")).toBe(false);
  });

  it("forgets the modules on sign-out and ignores an answer that arrives after it", async () => {
    let answer!: (response: Response) => void;
    vi.stubGlobal("fetch", vi.fn(() => new Promise<Response>(resolve => { answer = resolve; })));
    const loading = useModules().start();
    useModules().stop();
    answer(json({ modules: [{ id: "boards", version: 1 }] }));
    await loading;
    expect(useModules().enabled("boards")).toBe(false);
  });
});
