import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearCsrf } from "../api";
import { boardApi, type BoardChange } from "./boardApi";

let sent: RequestInit[] = [];
let refuseKeepalive = false;
const hidden = (value: boolean) => Object.defineProperty(document, "visibilityState", { configurable: true, get: () => (value ? "hidden" : "visible") });
beforeEach(() => {
  clearCsrf();
  sent = [];
  refuseKeepalive = false;
  vi.stubGlobal("fetch", vi.fn(async (input: string, init: RequestInit = {}) => {
    if (String(input).endsWith("/auth/csrf")) return Response.json({ token: "csrf-1" });
    sent.push(init);
    // The browser's answer to a keepalive body over its 64 KiB budget.
    if (init.keepalive && refuseKeepalive) throw new TypeError("Failed to execute 'fetch' on 'Window': Request body exceeds keepalive limit");
    return Response.json({ sequence: 3, duplicate: false });
  }));
});
afterEach(() => { vi.unstubAllGlobals(); hidden(false); });

/** A batch of Czech text: under 60 000 characters, over 64 KiB as UTF-8. */
const czech = (): BoardChange[] => Array.from({ length: 40 }, (_, index) => ({ type: "set", itemId: `n${index}`, text: "Příliš žluťoučký kůň úpěl ďábelské ódy. ".repeat(30) }));

describe("boardApi.submit (W4-1)", () => {
  it("sends a visible page's batch as an ordinary request", async () => {
    await boardApi.submit("b1", "op-1", [{ type: "rename", title: "Plán" }]);
    expect(sent.map(init => !!init.keepalive)).toEqual([false]);
  });

  it("keeps a hidden page's small batch alive, but measures the body in bytes, not characters", async () => {
    hidden(true);
    await boardApi.submit("b1", "op-1", [{ type: "rename", title: "Plán" }]);
    const changes = czech();
    expect(JSON.stringify({ operationId: "op-2", changes }).length).toBeLessThan(60_000);
    await boardApi.submit("b1", "op-2", changes);
    expect(sent.map(init => !!init.keepalive)).toEqual([true, false]);
  });

  it("sends once more without keepalive when the browser refuses the keepalive request", async () => {
    hidden(true);
    refuseKeepalive = true;
    await expect(boardApi.submit("b1", "op-1", [{ type: "rename", title: "Plán" }])).resolves.toEqual({ sequence: 3, duplicate: false });
    expect(sent.map(init => !!init.keepalive)).toEqual([true, false]);
  });
});
