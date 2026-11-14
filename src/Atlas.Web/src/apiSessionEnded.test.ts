// @vitest-environment node
import { afterEach, expect, it, vi } from "vitest";
import { api, onSessionEnded } from "./api";

afterEach(() => { vi.unstubAllGlobals(); onSessionEnded(undefined); });

const refused = (status: number) => vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ title: "Přihlaste se." }), { status, headers: { "Content-Type": "application/json" } })));

// UX SB4-8: a request refused as signed out tells the shell, which checks /auth/me and signs out with the reason.
it("tells the shell of a 401 outside the sign-in endpoints, and of nothing else", async () => {
  const ended = vi.fn();
  onSessionEnded(ended);
  refused(401);
  await api("/pages/p1").catch(() => {});
  expect(ended).toHaveBeenCalledTimes(1);
  // The sign-in itself (a wrong password, /auth/me while checking) and other refusals do not.
  await api("/auth/me").catch(() => {});
  refused(403);
  await api("/pages/p1").catch(() => {});
  expect(ended).toHaveBeenCalledTimes(1);
});
