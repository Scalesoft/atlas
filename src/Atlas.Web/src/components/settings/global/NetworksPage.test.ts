import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../../../api";
import type { TrustedAddress } from "../../../protection";
import { fakeUrlApi, mountPage } from "../general/testing";
import NetworksPage from "./NetworksPage.vue";

const proxy = (id: string): TrustedAddress => ({ id, name: `Proxy ${id}`, kind: null, range: "10.0.0.1/32", isActive: true, warnings: [] });
const connection = { address: "10.0.0.5", source: "direct", reason: "direct", peer: "10.0.0.5", peerIsProxy: false, hops: 0, forwardedFor: [], network: null, networkException: false, instanceMode: "always", reauthRequired: true, version: "3" };

let wrapper: VueWrapper | undefined;
async function open(activeProxies: number, hopLimit: number, isActive = true) {
  fakeUrlApi((_method, url) => {
    if (url.pathname === "/api/security/connection") return connection;
    if (url.pathname === "/api/security/networks") return { version: "3", items: [] };
    if (url.pathname === "/api/security/proxies") {
      return { version: "3", hopLimit, maxHopLimit: 5, items: Array.from({ length: activeProxies }, (_, index) => ({ ...proxy(`p-${index}`), isActive })), environment: { addresses: [], differs: false, recovery: false, importedAt: null } };
    }
    return new Response(null, { status: 404 });
  });
  const mounted = mountPage(NetworksPage, { adding: false }, { hash: "#/settings/security/networks" });
  wrapper = mounted.wrapper;
  await flushPromises();
  return mounted.wrapper;
}

beforeEach(() => clearCsrf());
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
});

describe("proxy and hop summary", () => {
  it.each([
    [1, 1, "1 důvěryhodná proxy · nejvýš 1 hop"],
    [3, 2, "3 důvěryhodné proxy · nejvýš 2 hopy"],
    [0, 5, "0 důvěryhodných proxy · nejvýš 5 hopů"],
  ])("reads %i proxies and a hop limit of %i in the right Czech forms", async (proxies, hops, summary) => {
    const view = await open(proxies, hops);
    expect(view.text()).toContain(summary);
  });
});

describe("proxy confirmation", () => {
  // UX-N-7: switching a proxy on widens whom the server believes X-Forwarded-For; the confirmation says so, not the opposite.
  it.each([
    [false, "Zapnout proxy „Proxy p-0“?", "začne číst"],
    [true, "Vypnout proxy „Proxy p-0“?", "přestane číst"],
  ])("a proxy that was active=%s reads its own direction", async (isActive, title, note) => {
    const view = await open(1, 1, isActive);
    await view.get('[aria-controls$="-content"]').trigger("click");
    await flushPromises();
    await view.get('[aria-label="Důvěřovat proxy Proxy p-0"]').trigger("click");
    await flushPromises();
    const confirmation = view.get('[data-proxy="p-0"] .network-confirm');
    expect(confirmation.text()).toContain(title);
    expect(confirmation.text()).toContain(note);
  });
});
