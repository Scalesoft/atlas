import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../../../api";
import McpModule from "./McpModule.vue";
import { mcpState } from "./moduleState";
import { moduleEntry } from "./moduleRegistry";
import { fakeApi, mountPage, problem, type FakeCall } from "./testing";

let wrapper: VueWrapper | undefined;
let calls: FakeCall[] = [];
async function open(initial: { enabled: boolean; url?: string | null; version: string | null }, put?: (body: unknown) => unknown) {
  calls = fakeApi((method, path, body) => {
    if (path === "/admin/mcp" && method === "GET") return initial;
    if (path === "/admin/mcp" && method === "PUT") return put ? put(body) : { enabled: true, url: "https://wiki.example/mcp", version: "v2" };
    return problem(404, `unexpected ${method} ${path}`);
  });
  const mounted = mountPage(McpModule, {});
  wrapper = mounted.wrapper;
  await flushPromises();
  return mounted.wrapper;
}
const saveButton = (view: VueWrapper) => view.findAll("button").find((item) => item.text().includes("Uložit nastavení"))!;

beforeEach(() => clearCsrf());
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  document.body.innerHTML = "";
});

describe("Server MCP", () => {
  it("is off with no address; turning it on shows the consequences and saves with the CAS version", async () => {
    const view = await open({ enabled: false, version: "v1" });
    expect(view.find("[data-mcp-url]").exists()).toBe(false);
    expect(view.find("[data-mcp-impact]").exists()).toBe(false);
    await view.get("[data-mcp-enabled]").trigger("click");
    expect(view.get("[data-mcp-impact]").text()).toContain("Tokeny s kanálem MCP začnou fungovat");
    expect(view.get("[data-mcp-impact]").text()).toContain("určuje publikace stránky");
    await saveButton(view).trigger("click");
    await flushPromises();
    expect(calls.find((call) => call.method === "PUT")!.body).toEqual({ enabled: true, expectedVersion: "v1" });
    expect(view.get("[data-mcp-status]").text()).toBe("Zapnuto");
    expect(view.get("[data-mcp-url]").text()).toBe("https://wiki.example/mcp");
    expect(view.find("[data-mcp-impact]").exists()).toBe(false);
  });

  it("shows the address only while enabled and warns before turning off", async () => {
    const view = await open({ enabled: true, url: "https://wiki.example/mcp", version: "v2" });
    expect(view.get("[data-mcp-url]").text()).toBe("https://wiki.example/mcp");
    await view.get("[data-mcp-enabled]").trigger("click");
    expect(view.get("[data-mcp-impact]").text()).toContain("přestanou fungovat");
  });

  it("keeps the draft and shows the conflict note on 409", async () => {
    const view = await open({ enabled: false, version: "v1" }, () => problem(409, "Nastavení bylo mezitím změněno."));
    await view.get("[data-mcp-enabled]").trigger("click");
    await saveButton(view).trigger("click");
    await flushPromises();
    expect(view.find("[data-module-conflict]").exists()).toBe(true);
    expect(view.find("[data-mcp-url]").exists()).toBe(false);
  });
});

describe("registry", () => {
  it("registers mcp with a plug icon and a state badge", async () => {
    const entry = moduleEntry("mcp")!;
    expect(entry.icon).toBe("plug");
    expect(mcpState({ enabled: true, version: "v" })).toMatchObject({ tone: "success", label: "Zapnuto" });
    expect(mcpState({ enabled: false, version: "v" })).toMatchObject({ tone: "neutral", label: "Vypnuto" });
    fakeApi(() => ({ enabled: false, version: "v1" }));
    expect((await entry.state!()).label).toBe("Vypnuto");
  });
});
